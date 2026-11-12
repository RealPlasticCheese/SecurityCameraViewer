using System.Globalization;

namespace RtspVideoHarness;

public static class FfmpegCommands
{
    public static string[] Decoder(VideoFile file, VideoFormat format) => Decoder(new MediaInput(file.FullPath), format);

    public static string[] Decoder(MediaInput input, VideoFormat format) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", .. InputArguments(input),
        "-map", "0:v:0", "-an", "-sn", "-dn", "-vf",
        FormattableString.Invariant($"scale={format.Width}:{format.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={format.Width}:{format.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={format.FramesPerSecond}"),
        "-pix_fmt", "yuv420p", "-f", "rawvideo", "pipe:1"
    ];

    public static string[] AudioDecoder(VideoFile file) => AudioDecoder(new MediaInput(file.FullPath));

    public static string[] AudioDecoder(MediaInput input) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", .. InputArguments(input),
        "-map", "0:a:0", "-vn", "-sn", "-dn", "-af", "aresample=async=1:first_pts=0",
        "-ar", "48000", "-ac", "2", "-f", "s16le", "pipe:1"
    ];

    public static string[] InputArguments(MediaInput input)
    {
        var arguments = new List<string>();
        if (input.Headers is { Count: > 0 })
        {
            foreach (var header in input.Headers)
            {
                if (header.Key.Length == 0 || header.Key.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                    header.Value.Any(character => char.IsControl(character) && character != '\t'))
                {
                    throw new InvalidDataException("Invalid HTTP request header in media metadata.");
                }
            }

            arguments.AddRange(["-headers", string.Concat(input.Headers.Select(header => $"{header.Key}: {header.Value}\r\n"))]);
        }

        if (input.LiveHls)
        {
            arguments.AddRange(["-live_start_index", "-1"]);
        }

        arguments.AddRange(["-i", input.Location]);
        return arguments.ToArray();
    }

    public static string[] Publisher(HarnessOptions options, string? audioInput = null)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "rawvideo", "-pixel_format", "yuv420p",
            "-video_size", FormattableString.Invariant($"{options.Format.Width}x{options.Format.Height}"),
            "-framerate", options.Format.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-probesize", "32", "-analyzeduration", "0", "-i", "pipe:0"
        };
        if (options.AudioEnabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audioInput);
            arguments.AddRange(["-f", "s16le", "-ar", "48000", "-ac", "2", "-probesize", "32", "-analyzeduration", "0", "-i", audioInput,
                "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a", "128k"]);
        }
        else
        {
            arguments.Add("-an");
        }

        arguments.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
        "-g", options.Format.FramesPerSecond.ToString(CultureInfo.InvariantCulture), "-bf", "0",
        "-f", "rtsp", "-rtsp_transport", "tcp", options.StreamUri.AbsoluteUri
        ]);
        return arguments.ToArray();
    }
}
