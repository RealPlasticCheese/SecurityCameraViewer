using System.Diagnostics;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class LivePassthroughFileTests
{
    public static async Task RunAsync()
    {
        foreach (var codec in new[] { "h264", "hevc", "hevc10" })
        {
            await VerifyPassthroughAsync(codec);
        }
    }

    private static async Task VerifyPassthroughAsync(string codec)
    {
        var hevc = codec != "h264";
        var directory = Path.GetFullPath(Path.Combine("artifacts", "passthrough-tests", codec, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "source.mkv");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string[] encoder = hevc
            ? ["-c:v", "libx265", "-preset", "fast", "-x265-params", "keyint=150:min-keyint=150:scenecut=0:bframes=2:open-gop=0:log-level=error",
                "-pix_fmt", codec == "hevc10" ? "yuv420p10le" : "yuv420p"]
            : ["-c:v", "libx264", "-preset", "fast", "-profile:v", "high", "-bf", "2",
                "-g", "150", "-keyint_min", "150", "-sc_threshold", "0", "-pix_fmt", "yuv420p"];
        await CommandAsync("ffmpeg", new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=15", "-t", "16", "-c:a", "pcm_mulaw" }
            .Concat(encoder).Append(input), deadline.Token);
        var runner = new FileInputProcess(input);
        var history = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 4 * 1024 * 1024);
        var fragments = new List<VideoFragment>();
        var captureOrigin = DateTimeOffset.UtcNow.AddSeconds(-16);
        LiveMediaInitialization? initialization = null;
        await new FfmpegLiveMediaSource(runner).RunAsync(new(), "", 1, item =>
        {
            initialization = item;
            history.Initialize(item);
            return Task.CompletedTask;
        }, item =>
        {
            fragments.Add(item);
            history.Add(item with
            {
                ProducedAt = captureOrigin.AddSeconds(item.PresentationEndSeconds ?? item.StartSeconds + item.DurationSeconds)
            });
            return Task.CompletedTask;
        }, deadline.Token);
        LivePerformanceTests.Check(runner.Calls == 1 && (hevc ? initialization!.H264Profile is null : initialization!.H264Profile == 100) && fragments.Count >= 10 &&
            fragments[0].StartsWithKeyframe && fragments.Any(fragment => !fragment.StartsWithKeyframe),
            $"Real {codec} with B-frames and fifteen-second GOPs stays on the passthrough path");
        LivePerformanceTests.Check(initialization!.HasAudio && initialization.MimeType.Contains("mp4a.40.2") &&
            initialization.MimeType.Contains(hevc ? "hev1." : "avc1."),
            "Passthrough preserves the source video codec while converting G.711 audio to AAC");
        var output = Path.Combine(directory, "copy.mp4");
        await File.WriteAllBytesAsync(output, initialization!.Bytes.Concat(fragments.SelectMany(fragment => fragment.Bytes)).ToArray(), deadline.Token);
        var hashes = new[] { "-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=data_hash",
            "-show_data_hash", "SHA256", "-of", "csv=p=0" };
        var originalHashes = await CommandAsync("ffprobe", hashes.Concat([input]), deadline.Token);
        var copiedHashes = await CommandAsync("ffprobe", hashes.Concat([output]), deadline.Token);
        LivePerformanceTests.Check(originalHashes == copiedHashes && originalHashes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 160,
            $"Every copied {codec} packet matches the original encoded payload without re-encoding or dropped frames");
        await CommandAsync("ffmpeg", ["-v", "error", "-xerror", "-i", output, "-map", "0:a:0", "-f", "null", "-"], deadline.Token);
        var range = history.Snapshot(1, "live", "").Ranges.Single();
        foreach (var seconds in new[] { 2.5, 13.5, 15.5 })
        {
            var playlist = history.Playlist(range.Epoch, range.Start + (long)(seconds * 1000))!;
            var bytes = initialization.Bytes.Concat(playlist.Fragments.SelectMany(fragment => history.Media(range.Epoch, fragment.Sequence)!)).ToArray();
            var path = Path.Combine(directory, $"seek-{seconds:0.0}.mp4");
            await File.WriteAllBytesAsync(path, bytes, deadline.Token);
            var errors = await CommandAsync("ffmpeg", ["-v", "error", "-xerror", "-i", path, "-map", "0:v:0", "-f", "null", "-"], deadline.Token);
            LivePerformanceTests.Check(errors.Length == 0, $"A real {codec} rewind seek between native keyframes decodes its complete dependency chain");
        }
    }

    private sealed class FileInputProcess(string path) : IFfmpegProcess
    {
        public int Calls
        {
            get; private set;
        }
        public Task RunAsync(IEnumerable<string> arguments, string? input, Func<Stream, CancellationToken, Task> read,
            CancellationToken token, string pipeline = "other", int channel = 0)
        {
            Calls++;
            var selected = arguments.ToArray();
            var map = Array.IndexOf(selected, "-map");
            return new FfmpegProcess("ffmpeg").RunAsync(new[] { "-i", path }.Concat(selected[map..]), null, read, token, pipeline, channel);
        }
    }

    private static async Task<string> CommandAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.Start();
        using var cancellation = token.Register(() => { if (!process.HasExited) { process.Kill(); } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var stderr = await error;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(executable + " failed: " + stderr);
        }
        return await output + stderr;
    }
}
