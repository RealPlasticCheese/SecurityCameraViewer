using System.Net;
using System.Text.Json;

namespace RtspVideoHarness.Tests;

internal static class UnitTests
{
    public static async Task RunAsync()
    {
        await OptionsAsync();
        await CatalogAsync();
        await FramesAsync();
        await PlaylistAsync();
        await TimingAsync();
        CommandsAndConfiguration();
        await Check.ThrowsAsync<IOException>(() => Task.Run(async () =>
        {
            await using var process = new ManagedProcess("missing-harness-executable-" + Guid.NewGuid(), [], "Missing dependency");
        }), "Missing child executable reports an actionable dependency failure");
    }

    private static async Task OptionsAsync()
    {
        var current = Path.GetFullPath("artifacts");
        var defaults = HarnessOptions.Parse([], current);
        Check.That(defaults.Folder == current && defaults.Port == 8554 && defaults.BlankPeriod == TimeSpan.Zero &&
            defaults.Order == FileOrder.Name && !defaults.Once && !defaults.OfflineGaps && defaults.AudioEnabled,
            "Defaults use caller directory, name order, no gap, audio and continuous looping");
        Check.That(HarnessOptions.Parse(["relative folder"], current).Folder == Path.Combine(current, "relative folder"),
            "Relative folders resolve against the caller directory");
        var all = HarnessOptions.Parse(["--folder", "video samples", "--order", "created", "--descending", "--recursive", "--once",
            "--blank-ms", "123", "--offline-gaps", "--port", "8555", "--listen", "0.0.0.0", "--path", "camera/main",
            "--size", "640x480", "--fps", "29", "--ffmpeg", "custom ffmpeg", "--ffprobe", "custom probe", "--mediamtx", "custom server",
            "--timeout-seconds", "45", "--extensions", ".MP4, .custom", "--no-audio"], current);
        Check.That(all.Descending && all.Recursive && all.Once && all.OfflineGaps && !all.AudioEnabled &&
            all.Order == FileOrder.Created && all.BlankPeriod.TotalMilliseconds == 123 && all.Format == new VideoFormat(640, 480, 29) &&
            all.StreamUri.AbsoluteUri == "rtsp://127.0.0.1:8555/camera/main" && all.FfmpegExecutable == "custom ffmpeg" &&
            all.FfprobeExecutable == "custom probe" && all.MediaMtxExecutable == "custom server" && all.ProcessTimeout.TotalSeconds == 45,
            "All options parse together, including port and audio control");
        Check.That(HarnessOptions.Parse(["--", "-samples"], current).Folder.EndsWith("-samples", StringComparison.Ordinal),
            "Option terminator permits a folder name starting with a dash");
        Check.That(HarnessOptions.Parse(["--help"]).ShowHelp && HarnessOptions.Parse(["-h"]).ShowHelp,
            "Both help options are recognized without a media folder");
        Check.That(!HarnessOptions.Parse(["--", "--help"], current).ShowHelp && !HarnessOptions.Parse(["--folder", "--help"], current).ShowHelp,
            "A literal folder named --help is not interpreted as a help request");
        Check.That(HarnessOptions.Parse(["--listen", "::", "--port", "65535"]).StreamUri.AbsoluteUri == "rtsp://[::1]:65535/sample",
            "IPv6 wildcard binding produces a valid local publisher URI");
        Check.That(HarnessOptions.Parse(["--listen", "192.0.2.1", "--port", "1", "--order", "modified"]).PublisherAddress.Equals(IPAddress.Parse("192.0.2.1")),
            "Specific bind IP and legal port boundaries are accepted");
        Check.That(HarnessOptions.Parse(["--blank-ms", "100000000000", "--extensions", "*"]).BlankPeriod.TotalMilliseconds == 100000000000,
            "Long gaps and arbitrary FFmpeg-supported extensions have no artificial cap");
        foreach (var args in new string[][]
        {
            ["--unknown"], ["a", "b"], ["a", "--folder", "b"], ["--folder"], ["--folder", ""],
            ["--order", "random"], ["--port", "0"], ["--port", "65536"], ["--port", "x"],
            ["--blank-ms", "-1"], ["--blank-ms", "0.5"], ["--blank-ms", "9223372036854775807"],
            ["--size", "0x480"], ["--size", "641x480"], ["--size", "640"], ["--size", "640x480x2"],
            ["--size", "2147483646x2147483646"], ["--fps", "0"], ["--fps", "-1"],
            ["--listen", "localhost"], ["--path", "/leading"], ["--path", "a//b"], ["--path", "../a"],
            ["--path", "a?b"], ["--extensions", "mp4"], ["--extensions", ",,"], ["--"], ["--", "a", "b"],
            ["--timeout-seconds", "0"], ["--timeout-seconds", "4294968"]
        })
        {
            await Check.ThrowsAsync<ArgumentException>(() => HarnessOptions.Parse(args), "Invalid options rejected: " + string.Join(' ', args));
        }
    }

    private static async Task CatalogAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "nested"));
        try
        {
            foreach (var name in new[] { "clip2.mp4", "clip10.MP4", "ignore.txt", "nested/clip1.mkv", "other.custom" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name), "fixture");
            }

            var options = new HarnessOptions { Folder = directory };
            var files = VideoCatalog.Discover(options);
            Check.That(files.Select(file => file.RelativePath).SequenceEqual(["clip10.MP4", "clip2.mp4"]),
                "Discovery filters extensions case-insensitively and uses file name order");
            Check.That(VideoCatalog.Discover(options with
            {
                Recursive = true
            }).Count == 3, "Recursive discovery includes nested videos");
            Check.That(VideoCatalog.Discover(options with
            {
                Extensions = [".custom"]
            }).Single().RelativePath == "other.custom",
                "Extension override accepts uncommon containers");
            Check.That(VideoCatalog.Discover(options with
            {
                Extensions = ["*"]
            }).Count == 4, "Wildcard extension filter includes all files");
            await Check.ThrowsAsync<DirectoryNotFoundException>(() => VideoCatalog.Discover(options with { Folder = Path.Combine(directory, "absent") }),
                "Missing folder fails before starting the server");
            await Check.ThrowsAsync<InvalidOperationException>(() => VideoCatalog.Discover(options with { Extensions = [".absent"] }),
                "Empty playlist has an actionable error");

            var epoch = DateTime.UnixEpoch;
            VideoFile[] fixture = [new("/a", "b.mp4", epoch, epoch.AddDays(2)), new("/b", "A.mp4", epoch, epoch),
                new("/c", "a.mp4", epoch.AddDays(1), epoch.AddDays(1))];
            Check.That(VideoCatalog.Sort(fixture, FileOrder.Name, false).Select(file => file.RelativePath).SequenceEqual(["A.mp4", "a.mp4", "b.mp4"]),
                "Ordinal tie-breaker makes file order deterministic");
            Check.That(VideoCatalog.Sort(fixture, FileOrder.Created, false).Select(file => file.RelativePath).SequenceEqual(["A.mp4", "b.mp4", "a.mp4"]),
                "Creation dates sort oldest first with name tie-breakers");
            Check.That(VideoCatalog.Sort(fixture, FileOrder.Modified, true).Select(file => file.RelativePath).SequenceEqual(["b.mp4", "a.mp4", "A.mp4"]),
                "Modification date order can be reversed");
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => VideoCatalog.Sort(fixture, (FileOrder)99, false), "Unknown catalog order fails");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task FramesAsync()
    {
        var black = BlackFrames.Create(new(4, 2, 25));
        Check.That(black.Length == 12 && black.Take(8).All(value => value == 16) && black.Skip(8).All(value => value == 128),
            "Black frames contain correct limited-range YUV420 planes");
        Check.That(BlackFrames.Count(TimeSpan.Zero, 25) == 0 && BlackFrames.Count(TimeSpan.FromMilliseconds(1), 25) == 1 &&
            BlackFrames.Count(TimeSpan.FromMilliseconds(40), 25) == 1 && BlackFrames.Count(TimeSpan.FromMilliseconds(41), 25) == 2,
            "Blank duration rounds up only to the actual frame interval");
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => BlackFrames.Count(TimeSpan.FromMilliseconds(-1), 25), "Negative black duration fails");
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => BlackFrames.Count(TimeSpan.Zero, 0), "Invalid black frame rate fails");
        await Check.ThrowsAsync<OverflowException>(() => BlackFrames.Count(TimeSpan.MaxValue, int.MaxValue), "Impossible frame counts report numeric overflow");
        var frames = new List<byte[]>();
        using var input = new PartialStream([1, 2, 3, 4, 5, 6, 7, 8]);
        var count = await VideoFrames.CopyAsync(input, 4, (frame, _) =>
        {
            frames.Add(frame.ToArray());
            return Task.CompletedTask;
        }, CancellationToken.None);
        Check.That(count == 2 && frames[0].SequenceEqual<byte>([1, 2, 3, 4]) && frames[1].SequenceEqual<byte>([5, 6, 7, 8]),
            "Partial pipe reads assemble complete frames in order");
        Check.That(await VideoFrames.CopyAsync(Stream.Null, 4, (_, _) => throw new Exception(), CancellationToken.None) == 0,
            "Empty decoder output reports no frames");
        await Check.ThrowsAsync<InvalidDataException>(() => VideoFrames.CopyAsync(new MemoryStream([1, 2, 3]), 4, (_, _) => Task.CompletedTask, CancellationToken.None),
            "Truncated raw frames cannot enter the encoder");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => VideoFrames.CopyAsync(new MemoryStream([1, 2, 3, 4]), 4, (_, _) => Task.CompletedTask, canceled.Token),
            "Frame reads respect cancellation");
        await Check.ThrowsAsync<IOException>(() => VideoFrames.CopyAsync(new MemoryStream([1, 2, 3, 4]), 4, (_, _) => throw new IOException("sink"), CancellationToken.None),
            "Publisher failure propagates through frame copying");
        await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => VideoFrames.CopyAsync(Stream.Null, 0, (_, _) => Task.CompletedTask, CancellationToken.None),
            "Zero-size raw frames are rejected");
        foreach (var fps in new[] { 1, 25, 29, 60, 100000 })
        {
            var clock = new AudioFrameClock(fps);
            long bytes = 0;
            for (var index = 0; index < fps; index++)
            {
                bytes += clock.NextByteCount();
            }

            Check.That(bytes == 48000 * 4, $"Audio clock has no rounding drift at {fps} fps");
        }

        Check.That((await AudioFrames.ReadAsync(null, 12, CancellationToken.None)).All(value => value == 0), "Absent audio produces PCM silence");
        using var audio = new PartialStream([1, 2, 3, 4]);
        Check.That((await AudioFrames.ReadAsync(audio, 8, CancellationToken.None)).SequenceEqual<byte>([1, 2, 3, 4, 0, 0, 0, 0]),
            "Short audio is preserved and padded with silence");
        using var longer = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Check.That((await AudioFrames.ReadAsync(longer, 4, CancellationToken.None)).SequenceEqual<byte>([1, 2, 3, 4]) && longer.Position == 4,
            "Audio reads consume exactly the video frame's duration");
        await Check.ThrowsAsync<OperationCanceledException>(() => AudioFrames.ReadAsync(new MemoryStream([1, 2]), 4, canceled.Token),
            "Audio decoding respects cancellation");
    }

    private static async Task PlaylistAsync()
    {
        var files = new[] { new VideoFile("a", "a", DateTime.UnixEpoch, DateTime.UnixEpoch), new VideoFile("b", "b", DateTime.UnixEpoch, DateTime.UnixEpoch) };
        var playback = new Playback();
        var options = new HarnessOptions { Once = true };
        await new PlaylistPlayer(playback).RunAsync(files, options, CancellationToken.None);
        Check.That(playback.Calls.SequenceEqual(["a", "b"]), "Zero gap streams files without blanks or disconnects");
        playback.Calls.Clear();
        await new PlaylistPlayer(playback).RunAsync(files, options with
        {
            OfflineGaps = true
        }, CancellationToken.None);
        Check.That(playback.Calls.SequenceEqual(["a", "b"]), "Offline switch with zero gap preserves continuity");
        playback.Calls.Clear();
        options = options with
        {
            BlankPeriod = TimeSpan.FromMilliseconds(2)
        };
        await new PlaylistPlayer(playback).RunAsync(files, options, CancellationToken.None);
        Check.That(playback.Calls.SequenceEqual(["a", "black", "b"]), "Black gap occurs between files with no trailing gap in once mode");
        playback.Calls.Clear();
        await new PlaylistPlayer(playback).RunAsync(files, options with
        {
            OfflineGaps = true
        }, CancellationToken.None);
        Check.That(playback.Calls.SequenceEqual(["a", "offline", "b"]), "Offline gap disconnects before the next file");
        playback.Calls.Clear();
        await new PlaylistPlayer(playback).RunAsync([files[0]], options, CancellationToken.None);
        Check.That(playback.Calls.SequenceEqual(["a"]), "Single-file once mode has no unnecessary gap");
        using var stop = new CancellationTokenSource();
        playback = new Playback
        {
            OnPlay = count =>
        {
            if (count == 3)
            {
                stop.Cancel();
            }
        }
        };
        await Check.ThrowsAsync<OperationCanceledException>(() => new PlaylistPlayer(playback).RunAsync(files, options with { Once = false }, stop.Token),
            "Looping playlist responds to cancellation");
        Check.That(playback.Calls.Take(5).SequenceEqual(["a", "black", "b", "black", "a"]), "Loop boundary receives the same configured gap");
        await Check.ThrowsAsync<ArgumentException>(() => new PlaylistPlayer(new Playback()).RunAsync([], options, CancellationToken.None), "Empty playlists cannot spin indefinitely");
        await Check.ThrowsAsync<IOException>(() => new PlaylistPlayer(new Playback { Fail = true }).RunAsync(files, options, CancellationToken.None),
            "Corrupt file or playback failure stops the playlist");
    }

    private static async Task TimingAsync()
    {
        var time = new ManualTimeProvider();
        var pacer = new FramePacer(25, time);
        await pacer.WaitAsync(CancellationToken.None);
        var wait = pacer.WaitAsync(CancellationToken.None);
        Check.That(!wait.IsCompleted && time.Pending == 1, "Frame pacer schedules the next frame instead of playing as fast as possible");
        time.Advance(TimeSpan.FromMilliseconds(40));
        await wait;
        time.Advance(TimeSpan.FromSeconds(1));
        Check.That(pacer.WaitAsync(CancellationToken.None).IsCompletedSuccessfully, "Decoder pauses do not trigger an extra frame delay");
        using var stop = new CancellationTokenSource();
        var canceledWait = pacer.WaitAsync(stop.Token);
        stop.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => canceledWait, "Frame pacing can be canceled immediately");

        time = new ManualTimeProvider();
        var playback = new Playback();
        var files = new[] { new VideoFile("a", "a", DateTime.UnixEpoch, DateTime.UnixEpoch), new VideoFile("b", "b", DateTime.UnixEpoch, DateTime.UnixEpoch) };
        var running = new PlaylistPlayer(playback, time).RunAsync(files, new HarnessOptions
        {
            Once = true,
            OfflineGaps = true,
            BlankPeriod = TimeSpan.FromDays(60)
        }, CancellationToken.None);
        Check.That(!running.IsCompleted && playback.Calls.SequenceEqual(["a", "offline"]), "Offline gaps delay the next publisher");
        time.Advance(TimeSpan.FromMilliseconds(uint.MaxValue - 1L));
        for (var index = 0; index < 100 && time.Pending == 0; index++)
        {
            await Task.Delay(1);
        }

        Check.That(time.Pending == 1, "Long offline gaps split at the real timer limit");
        time.Advance(TimeSpan.FromDays(60));
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check.That(playback.Calls.SequenceEqual(["a", "offline", "b"]), "Long gaps complete without a feature limit");
    }

    private static void CommandsAndConfiguration()
    {
        var options = new HarnessOptions { Port = 8555, StreamPath = "test/stream" };
        var file = new VideoFile("a folder/video's file.mp4", "file", DateTime.UnixEpoch, DateTime.UnixEpoch);
        var decoder = FfmpegCommands.Decoder(file, options.Format);
        Check.That(decoder.Contains(file.FullPath) && decoder.Contains("0:v:0") && decoder.Contains("yuv420p") && !decoder.Contains("-re"),
            "Decoder arguments preserve literal paths and normalize video without duplicate pacing");
        Check.That(FfmpegCommands.AudioDecoder(file).Contains(file.FullPath) && FfmpegCommands.AudioDecoder(file).Contains("s16le"),
            "Audio decoder normalizes to stereo PCM");
        var publisher = FfmpegCommands.Publisher(options, "tcp://127.0.0.1:12345");
        Check.That(publisher.Contains("aac") && publisher.Contains("libx264") && publisher.Contains("tcp") && publisher.Last() == "rtsp://127.0.0.1:8555/test/stream",
            "Publisher maintains H.264/AAC over the configured RTSP TCP endpoint");
        Check.That(!FfmpegCommands.Publisher(options with
        {
            AudioEnabled = false
        }).Contains("aac"), "Video-only publishing opens no audio input");
        using var config = JsonDocument.Parse(MediaMtxServer.Configuration(options));
        var root = config.RootElement;
        Check.That(root.GetProperty("rtspAddress").GetString() == "127.0.0.1:8555" && root.GetProperty("rtspTransports").GetArrayLength() == 1 &&
            root.GetProperty("paths").GetProperty("test/stream").GetProperty("source").GetString() == "publisher",
            "Server owns only the configured RTSP port and path");
        Check.That(new[] { "api", "metrics", "pprof", "playback", "rtmp", "hls", "webrtc", "srt", "moq" }.All(name => !root.GetProperty(name).GetBoolean()),
            "Multiple instances cannot collide through auxiliary MediaMTX listeners");
        Check.That(root.GetProperty("authInternalUsers")[1].GetProperty("ips")[0].GetString() == "127.0.0.1", "Only the local encoder may publish");
        using var ipv6 = JsonDocument.Parse(MediaMtxServer.Configuration(options with
        {
            ListenAddress = IPAddress.IPv6Any
        }));
        Check.That(ipv6.RootElement.GetProperty("rtspAddress").GetString() == "[::]:8555", "Server formats IPv6 bind addresses correctly");
    }

    private sealed class PartialStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
    }

    private sealed class Playback : IVideoPlayback
    {
        public List<string> Calls { get; } = [];
        public Action<int>? OnPlay
        {
            get; init;
        }
        public bool Fail
        {
            get; init;
        }
        private int playCount;

        public Task PlayFileAsync(VideoFile file, CancellationToken cancellationToken)
        {
            Calls.Add(file.RelativePath);
            OnPlay?.Invoke(++playCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Fail ? Task.FromException(new IOException("bad video")) : Task.CompletedTask;
        }

        public Task PlayBlackAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("black");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("offline");
            return Task.CompletedTask;
        }
    }
}
