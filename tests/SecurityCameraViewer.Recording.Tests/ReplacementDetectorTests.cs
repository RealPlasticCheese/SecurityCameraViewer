using System.Collections.Concurrent;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

internal static class ReplacementDetectorTests
{
    public static async Task RunAsync(string directory)
    {
        var factory = new SwitchDetectorFactory();
        var source = new MemorySource();
        var sink = new MemorySink();
        await using var recorder = new RecordingCamera(new ViewerSettings(), "", 1,
            new RecordingSettings { Directory = directory, PreCaptureSeconds = 0, PostCaptureSeconds = 1, MaximumEventSeconds = 60 }, factory, source, source, sink);
        await Task.Delay(200);
        if (!sink.Writers.IsEmpty)
        {
            throw new Exception("An idle replacement detector created an event.");
        }

        factory.Motion = true;
        await WaitAsync(() => sink.Writers.Count >= 2);
        factory.Motion = false;
        await WaitAsync(() => sink.Writers.All(w => w.Completed));
        if (sink.Writers.Any(w => w.Seconds > 61))
        {
            throw new Exception("Continuous activity did not split at the clip limit.");
        }

        Console.WriteLine("PASS: A replacement detector drives the unchanged recorder; continuous activity splits and quiet tails finish");
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }
    private sealed class SwitchDetectorFactory : IMotionDetectorFactory
    {
        public volatile bool Motion;
        public string Name => "Replacement test detector";
        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone) => new Detector(this);
        private sealed class Detector(SwitchDetectorFactory owner) : IMotionDetector
        {
            public MotionSample Analyse(ReadOnlySpan<byte> pixels, int width, int height, double seconds, DateTimeOffset capturedAt) => new(owner.Motion, owner.Motion ? 100 : 0, false);
        }
    }
    private sealed class MemorySource : IMotionFrameSource, IEncodedVideoSource
    {
        public async Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], int, int, Task> frame, CancellationToken token)
        {
            var still = new byte[16];
            while (true)
            {
                await frame(still, 4, 4);
                await Task.Delay(10, token);
            }
        }
        public async Task RunAsync(ViewerSettings connection, string password, int channel, Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            await initialized([1]);
            // Accelerated media clock exercises clip length without a one-minute wall-clock test.
            while (true)
            {
                await fragment(new([2], 1));
                await Task.Delay(10, token);
            }
        }
    }
    private sealed class MemorySink : IRecordingEventStore
    {
        public ConcurrentBag<Writer> Writers { get; } = [];
        public Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization, double preSeconds, CancellationToken token, DateTimeOffset? triggeredAt = null)
        {
            var writer = new Writer();
            Writers.Add(writer);
            return Task.FromResult<IEventWriter>(writer);
        }
        public sealed class Writer : IEventWriter
        {
            public volatile bool Completed;
            public double Seconds;
            public Task AppendAsync(VideoFragment fragment, CancellationToken token)
            {
                Seconds += fragment.DurationSeconds;
                return Task.CompletedTask;
            }
            public Task CompleteAsync(bool interrupted, CancellationToken token)
            {
                Completed = true;
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
