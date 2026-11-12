using System.Globalization;
using System.Net;

namespace RtspVideoHarness;

public enum FileOrder
{
    Name,
    Created,
    Modified
}

public sealed record VideoFormat(int Width, int Height, int FramesPerSecond)
{
    public int FrameBytes
    {
        get
        {
            var pixels = (long)Width * Height;
            var bytes = pixels + pixels / 2;
            if (Width < 2 || Height < 2 || Width % 2 != 0 || Height % 2 != 0 ||
                FramesPerSecond < 1 || bytes > Array.MaxLength)
            {
                throw new ArgumentException("YUV420 video needs positive, even dimensions, a positive frame rate, and a frame that fits a .NET array.");
            }

            return (int)bytes;
        }
    }
}

public sealed record HarnessOptions
{
    public bool ShowHelp
    {
        get; init;
    }
    public string Folder { get; init; } = Environment.CurrentDirectory;
    public Uri? YouTubeUrl
    {
        get; init;
    }
    public string YtDlpExecutable
    {
        get; init;
    } = File.Exists(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp"))
        ? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp") : "yt-dlp";
    public string? YouTubeFormat
    {
        get; init;
    }
    public FileOrder Order { get; init; } = FileOrder.Name;
    public bool Descending
    {
        get; init;
    }
    public bool Recursive
    {
        get; init;
    }
    public bool Once
    {
        get; init;
    }
    public bool OfflineGaps
    {
        get; init;
    }
    public bool AudioEnabled { get; init; } = true;
    public TimeSpan BlankPeriod
    {
        get; init;
    }
    public int Port { get; init; } = 8554;
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public string StreamPath { get; init; } = "sample";
    public VideoFormat Format { get; init; } = new(1280, 720, 25);
    public string FfmpegExecutable { get; init; } = "ffmpeg";
    public string? FfprobeExecutable
    {
        get; init;
    }
    public string MediaMtxExecutable { get; init; } = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mediamtx.exe" : "mediamtx");
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public string[] Extensions { get; init; } = [".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".asf", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".vob", ".ogv", ".3gp", ".h264", ".264", ".h265", ".hevc"];

    public IPAddress PublisherAddress => ListenAddress.Equals(IPAddress.Any) ? IPAddress.Loopback :
        ListenAddress.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : ListenAddress;

    public Uri StreamUri => new UriBuilder("rtsp", PublisherAddress.ToString(), Port, StreamPath).Uri;

    public static HarnessOptions Parse(string[] args, string? currentDirectory = null)
    {
        var options = new HarnessOptions { Folder = currentDirectory ?? Environment.CurrentDirectory };
        var inputSeen = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string Value()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                {
                    throw new ArgumentException($"{argument} requires a value.");
                }

                return args[index];
            }

            switch (argument)
            {
                case "--help":
                case "-h":
                    options = options with
                    {
                        ShowHelp = true
                    };
                    break;
                case "--folder":
                    SetFolder(Value());
                    break;
                case "--youtube":
                    SetYouTube(Value());
                    break;
                case "--yt-dlp":
                    options = options with
                    {
                        YtDlpExecutable = Value()
                    };
                    break;
                case "--youtube-format":
                    options = options with
                    {
                        YouTubeFormat = Value()
                    };
                    break;
                case "--order":
                    options = options with
                    {
                        Order = Value().ToLowerInvariant() switch
                        {
                            "name" => FileOrder.Name,
                            "created" => FileOrder.Created,
                            "modified" => FileOrder.Modified,
                            _ => throw new ArgumentException("--order must be name, created, or modified.")
                        }
                    };
                    break;
                case "--descending":
                    options = options with
                    {
                        Descending = true
                    };
                    break;
                case "--recursive":
                    options = options with
                    {
                        Recursive = true
                    };
                    break;
                case "--once":
                    options = options with
                    {
                        Once = true
                    };
                    break;
                case "--offline-gaps":
                    options = options with
                    {
                        OfflineGaps = true
                    };
                    break;
                case "--no-audio":
                    options = options with
                    {
                        AudioEnabled = false
                    };
                    break;
                case "--blank-ms":
                    var milliseconds = Number(Value(), argument, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond);
                    options = options with
                    {
                        BlankPeriod = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond)
                    };
                    break;
                case "--port":
                    options = options with
                    {
                        Port = (int)Number(Value(), argument, IPEndPoint.MinPort + 1, IPEndPoint.MaxPort)
                    };
                    break;
                case "--listen":
                    if (!IPAddress.TryParse(Value(), out var address))
                    {
                        throw new ArgumentException("--listen requires an IPv4 or IPv6 address (for example 0.0.0.0).");
                    }

                    options = options with
                    {
                        ListenAddress = address
                    };
                    break;
                case "--path":
                    var path = Value();
                    if (path.Split('/').Any(segment => segment.Length == 0 || segment.Any(character =>
                        !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')) ||
                        path.Split('/').Any(segment => segment is "." or ".."))
                    {
                        throw new ArgumentException("--path needs nonempty segments containing letters, digits, '.', '_' or '-'.");
                    }

                    options = options with
                    {
                        StreamPath = path
                    };
                    break;
                case "--size":
                    var dimensions = Value().Split('x', 'X');
                    if (dimensions.Length != 2)
                    {
                        throw new ArgumentException("--size must be WIDTHxHEIGHT (for example 1280x720).");
                    }

                    options = options with
                    {
                        Format = options.Format with
                        {
                            Width = (int)Number(dimensions[0], argument, 2, int.MaxValue),
                            Height = (int)Number(dimensions[1], argument, 2, int.MaxValue)
                        }
                    };
                    break;
                case "--fps":
                    options = options with
                    {
                        Format = options.Format with
                        {
                            FramesPerSecond = (int)Number(Value(), argument, 1, int.MaxValue)
                        }
                    };
                    break;
                case "--ffmpeg":
                    options = options with
                    {
                        FfmpegExecutable = Value()
                    };
                    break;
                case "--ffprobe":
                    options = options with
                    {
                        FfprobeExecutable = Value()
                    };
                    break;
                case "--mediamtx":
                    options = options with
                    {
                        MediaMtxExecutable = Value()
                    };
                    break;
                case "--timeout-seconds":
                    // Task.WaitAsync/Task.Delay use a 32-bit millisecond timer.
                    options = options with
                    {
                        ProcessTimeout = TimeSpan.FromSeconds(Number(Value(), argument, 1, (uint.MaxValue - 1L) / 1000))
                    };
                    break;
                case "--extensions":
                    var extensions = Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (extensions.Length == 0 || extensions.Any(extension => extension != "*" &&
                        (extension.Length < 2 || extension[0] != '.' || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character)))))
                    {
                        throw new ArgumentException("--extensions requires comma-separated extensions such as .mp4,.mkv, or '*'.");
                    }

                    options = options with
                    {
                        Extensions = extensions
                    };
                    break;
                case "--":
                    if (++index != args.Length - 1)
                    {
                        throw new ArgumentException("Use -- followed by exactly one folder path or YouTube URL.");
                    }

                    SetInput(args[index]);
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        throw new ArgumentException($"Unknown option: {argument}. Use --help for usage.");
                    }

                    SetInput(argument);
                    break;
            }
        }

        _ = options.Format.FrameBytes;
        _ = BlackFrames.Count(options.BlankPeriod, options.Format.FramesPerSecond);
        return options with
        {
            Folder = Path.GetFullPath(options.Folder, currentDirectory ?? Environment.CurrentDirectory)
        };

        void SetFolder(string folder)
        {
            if (inputSeen)
            {
                throw new ArgumentException("Specify only one video folder or YouTube URL.");
            }

            inputSeen = true;
            options = options with
            {
                Folder = folder
            };
        }

        void SetYouTube(string value)
        {
            if (inputSeen)
            {
                throw new ArgumentException("Specify only one video folder or YouTube URL.");
            }

            options = options with
            {
                YouTubeUrl = YouTubeAddress.Parse(value)
            };
            inputSeen = true;
        }

        void SetInput(string value)
        {
            if (value.Contains("://", StringComparison.Ordinal))
            {
                SetYouTube(value);
            }
            else
            {
                SetFolder(value);
            }
        }
    }

    private static long Number(string text, string option, long minimum, long maximum)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{option} requires an integer from {minimum} to {maximum}.");
        }

        return value;
    }
}
