using System.Collections.Concurrent;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

internal static class ContinuousRecordingTests
{
    public static async Task RunAsync(string directory)
    {
        var camera = new CameraRecordingSettings { Channel = 1, Mode = RecordingModes.Continuous };
        var settings = new RecordingSettings { Directory = directory, MaximumEventSeconds = 60, Cameras = [camera] };
        var source = new ControlledSource();
        var store = new MemoryStore();
        await using (var recorder = new RecordingCamera(new ViewerSettings(), "", 1, settings,
            new RejectDetection(), source, source, store))
        {
            await source.VideoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.VideoAsync(30);
            await source.VideoAsync(30);
            await source.VideoAsync(1);
            Check(store.Writers.Count == 2 && store.Writers[0].Seconds == 60 && store.Writers[1].Seconds == 1,
                "Continuous capture splits without duplicating or dropping a boundary fragment");
            Check(store.Triggers.All(trigger => trigger.Source == "continuous") && recorder.State.BufferBytes == 0 && source.Frame is null,
                "Continuous recording needs neither software detection nor a RAM pre-event buffer");
            var marker = new RecordingTrigger("sensor", "Door opened");
            Check(recorder.TryTrigger(marker, 20, false), "External triggers can mark an active continuous recording");
            await source.VideoAsync(1);
            Check(store.Writers[1].Triggers.Single().Id == marker.Id && store.Writers.Count == 2,
                "An external marker reuses existing footage instead of creating another recording");
            recorder.SetArming(new("disarmed", DateTimeOffset.UtcNow.AddMinutes(1)));
            Check(!recorder.TryTrigger(new("sensor", "Door opened"), 20, true),
                "External marker requests cannot override disarming or restart continuous capture");
            await source.VideoAsync(1);
            Check(store.Writers[1].Completed && store.Writers[1].Seconds == 2,
                "Disarming closes continuous capture before appending outside its policy");
            recorder.SetArming(new("armed", DateTimeOffset.UtcNow.AddMinutes(1)));
            await source.VideoAsync(1);
            Check(store.Writers.Count == 3, "Temporary arming resumes continuous recording");
        }
        Check(store.Writers[^1].Interrupted, "Shutdown preserves an interrupted continuous segment");

        var tomorrow = ((int)DateTime.UtcNow.DayOfWeek + 1) % 7;
        var scheduled = camera with
        {
            StreamSchedule = [new("", "", [tomorrow], true)]
        };
        source = new ControlledSource();
        store = new MemoryStore();
        await using (var recorder = new RecordingCamera(new ViewerSettings(), "", 1,
            settings with
            {
                TimeZoneId = "UTC",
                Cameras = [scheduled]
            }, new RejectDetection(), source, source, store))
        {
            await source.VideoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.VideoAsync(1);
            Check(store.Writers.Count == 0 && recorder.State.Status == "scheduled", "Continuous capture respects its weekly recording schedule");
            recorder.SetArming(new("armed", DateTimeOffset.UtcNow.AddMinutes(1)));
            await source.VideoAsync(1);
            Check(store.Writers.Count == 1, "Temporary arming overrides the continuous schedule");
        }

        source = new ControlledSource();
        store = new MemoryStore();
        await using (var recorder = new RecordingCamera(new ViewerSettings(), "", 1,
            settings with
            {
                Cameras = [camera with { DetectDuringContinuousRecording = true }]
            }, new MarkerDetectorFactory(), source, source, store))
        {
            await source.VideoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.FrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await source.VideoAsync(1);
            await source.FrameAsync(1);
            await source.FrameAsync(0);
            await source.FrameAsync(1);
            await source.VideoAsync(1);
            Check(store.Writers.Count == 1 && store.Writers[0].Triggers.Count == 2 && recorder.State.BufferBytes == 0,
                "Optional detection records repeated timestamped motion markers without duplicate video or RAM buffering");
            await source.FrameAsync(0);
            await source.VideoAsync(1);
            Check(!store.Writers[0].Completed, "A quiet scene never stops continuous recording");
        }

        var legacy = new CameraRecordingSettings { RecordWhenStreaming = true };
        Check(legacy.EffectiveMode == RecordingModes.AvailableVideo && (camera with
        {
            Mode = "unknown"
        }).EffectiveMode == "unknown" &&
            (settings with { Cameras = [camera with { Mode = "unknown" }] }).Validate() is not null,
            "Legacy available-video settings remain supported and invalid recording modes are rejected");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        Console.WriteLine("PASS: " + message);
    }

    private sealed class RejectDetection : IMotionDetectorFactory
    {
        public string Name => "Detection must stay off";
        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone) =>
            throw new InvalidOperationException("Continuous recording unexpectedly started detection.");
    }

    private sealed class MarkerDetectorFactory : IMotionDetectorFactory
    {
        public string Name => "Marker detector";
        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone) => new Detector();
        private sealed class Detector : IMotionDetector
        {
            public MotionSample Analyse(ReadOnlySpan<byte> pixels, int width, int height, double seconds, DateTimeOffset capturedAt) =>
                new(pixels[0] != 0, 0, false);
        }
    }

    private sealed class ControlledSource : IMotionFrameSource, IEncodedVideoSource
    {
        public TaskCompletionSource VideoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FrameStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<byte[], int, int, Task>? Frame
        {
            get; private set;
        }
        private Func<VideoFragment, Task>? _fragment;
        public Task VideoAsync(double seconds) => _fragment!(new([2], seconds));
        public Task FrameAsync(byte value) => Frame!([value], 1, 1);

        public async Task RunAsync(ViewerSettings connection, string password, int channel,
            Func<byte[], int, int, Task> frame, CancellationToken token)
        {
            Frame = frame;
            FrameStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        public async Task RunAsync(ViewerSettings connection, string password, int channel,
            Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            await initialized([1]);
            _fragment = fragment;
            VideoStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }

    private sealed class MemoryStore : IRecordingEventStore
    {
        public List<Writer> Writers { get; } = [];
        public List<RecordingTrigger> Triggers { get; } = [];
        public Task<IEventWriter> BeginTriggeredAsync(int channel, string name, byte[] initialization, double preSeconds,
            RecordingTrigger trigger, CancellationToken token, DateTimeOffset? triggeredAt = null)
        {
            var writer = new Writer();
            Writers.Add(writer);
            Triggers.Add(trigger);
            return Task.FromResult<IEventWriter>(writer);
        }

        public Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization, double preSeconds,
            CancellationToken token, DateTimeOffset? triggeredAt = null) =>
            BeginTriggeredAsync(channel, name, initialization, preSeconds, new(), token, triggeredAt);

        public sealed class Writer : IEventWriter
        {
            public double Seconds
            {
                get; private set;
            }
            public bool Completed
            {
                get; private set;
            }
            public bool Interrupted
            {
                get; private set;
            }
            public List<RecordingTrigger> Triggers { get; } = [];
            public Task AppendAsync(VideoFragment fragment, CancellationToken token)
            {
                Seconds += fragment.DurationSeconds;
                return Task.CompletedTask;
            }
            public Task AddTriggerAsync(RecordingTrigger trigger, CancellationToken token)
            {
                Triggers.Add(trigger);
                return Task.CompletedTask;
            }
            public Task CompleteAsync(bool interrupted, CancellationToken token)
            {
                Completed = true;
                Interrupted = interrupted;
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
