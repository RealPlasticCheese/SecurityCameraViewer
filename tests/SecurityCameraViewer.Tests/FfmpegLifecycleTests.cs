using System.Text.Json;
using SecurityCameraViewer.Core;

internal static class FfmpegLifecycleTests
{
    private const string SourceVariable = "SCV_TEST_FFMPEG_SOURCE";
    private sealed record Source(string Payload, string Error, int DelayMilliseconds = 0);

    // The test apphost doubles as a deterministic child process with the same stdin/stdout contract.
    public static async Task<int> SourceAsync()
    {
        var source = JsonSerializer.Deserialize<Source>(File.ReadAllText(Environment.GetEnvironmentVariable(SourceVariable)!))!;
        await Console.In.ReadToEndAsync();
        await Console.Error.WriteAsync(source.Error);
        await Console.OpenStandardOutput().WriteAsync(await File.ReadAllBytesAsync(source.Payload));
        await Task.Delay(source.DelayMilliseconds);
        return 0;
    }

    public static bool IsSource(string[] args) => args.FirstOrDefault() == "-hide_banner" && Environment.GetEnvironmentVariable(SourceVariable) is not null;

    public static async Task RunAsync()
    {
        var input = FfmpegSession.CreateInput(new(), "sp ace'@:/\\\n", 1);
        StreamProcessingTests.Check(input.Contains("sp%20ace%27%40%3A%2F%5C%0A", StringComparison.Ordinal),
            "FFmpeg input escapes reserved credential characters, including newlines");
        var startInfo = FfmpegSession.CreateStartInfo();
        StreamProcessingTests.Check(startInfo.ArgumentList.All(argument => !argument.Contains("rtsp://", StringComparison.Ordinal)),
            "Decoder arguments do not expose camera credentials");
        var root = Path.GetFullPath(Path.Combine("artifacts", "pipe-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var configuration = Path.Combine(root, "source.json");
        var payload = Path.Combine(root, "payload.bin");
        var executable = Path.ChangeExtension(typeof(FfmpegLifecycleTests).Assembly.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        var previous = Environment.GetEnvironmentVariable(SourceVariable);
        Environment.SetEnvironmentVariable(SourceVariable, configuration);
        try
        {
            async Task Configure(byte[] bytes, string error = "", int delay = 0)
            {
                await File.WriteAllBytesAsync(payload, bytes);
                await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new Source(payload, error, delay)));
            }
            async Task Complete(FfmpegSession session)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!session.IsCompleted)
                {
                    await Task.Delay(10, timeout.Token);
                }
            }
            byte[] jpeg = [0xff, 0xd8, 7, 0xff, 0xd9];
            byte[] earlierJpeg = [0xff, 0xd8, 3, 0xff, 0xd9];
            await Configure([.. earlierJpeg, .. jpeg, 0xff, 0xd8, 9]);
            await using (var session = new FfmpegSession(new(), "secret", 1, executable))
            {
                await Complete(session);
                StreamProcessingTests.Check(session.ConsumeFrame(frame => StreamProcessingTests.Check(frame.SequenceEqual(jpeg), "Latest complete JPEG child output is preserved", false), out var timestamp) && timestamp > 0,
                    "FFmpeg JPEG session retains the latest complete frame and ignores the partial EOF tail");
                StreamProcessingTests.Check(!session.ConsumeFrame(_ => throw new Exception("Duplicate frame"), out _), "Consuming a frame clears its pending state");
                StreamProcessingTests.Check(session.Failure == "Camera connection ended", "EOF has the expected diagnostic", false);
            }
            foreach (var bytes in new byte[][] { [], [0xff, 0xd8, 7] })
            {
                await Configure(bytes);
                await using var session = new FfmpegSession(new(), "secret", 1, executable);
                await Complete(session);
                StreamProcessingTests.Check(!session.ConsumeFrame(_ => throw new Exception("Partial JPEG published"), out _),
                    "Empty or incomplete child output never publishes a JPEG frame");
            }
            var oversized = new byte[JpegFrames.MaximumFrameBytes + 1];
            oversized[0] = 0xff;
            oversized[1] = 0xd8;
            await Configure(oversized);
            await using (var session = new FfmpegSession(new(), "secret", 1, executable))
            {
                await Complete(session);
                StreamProcessingTests.Check(session.Failure == "Playback stopped (InvalidDataException)" && !session.ConsumeFrame(_ => { }, out _),
                    "Oversized JPEG output stops the session without publishing a frame");
            }
            foreach (var (error, expected) in new[]
            {
                ("401 Unauthorized", "RTSP login rejected"), ("403 Forbidden", "RTSP viewing permission denied"),
                ("404 Not Found", "Camera stream path not found"), ("Connection refused", "Recorder refused"), ("unknown", "Camera connection ended")
            })
            {
                await Configure([], new string('x', 2040) + error + new string('x', 200));
                await using var session = new FfmpegSession(new(), "secret", 1, executable);
                await Complete(session);
                StreamProcessingTests.Check(session.Failure.StartsWith(expected, StringComparison.Ordinal), "Split stderr diagnostics are classified without leaking raw input", false);
            }
            await Configure(jpeg, delay: 30000);
            var running = new FfmpegSession(new(), "secret", 1, executable);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (!running.ConsumeFrame(_ => { }, out _))
                {
                    await Task.Delay(10, timeout.Token);
                }
            }
            await running.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await running.DisposeAsync();
            StreamProcessingTests.Check(running.IsCompleted && !running.ConsumeFrame(_ => { }, out _), "Session disposal cancels blocked stdout/stderr, reaps its child and releases frames");
            await using var missing = new FfmpegSession(new(), "secret", 1, executable: Path.Combine(root, "missing-ffmpeg"));
            await Complete(missing);
            StreamProcessingTests.Check(missing.Failure.StartsWith("FFmpeg could not start", StringComparison.Ordinal), "Missing FFmpeg produces a bounded diagnostic");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SourceVariable, previous);
        }
    }
}
