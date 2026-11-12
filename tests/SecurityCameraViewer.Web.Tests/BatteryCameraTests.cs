using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class BatteryCameraTests
{
    private static void Check(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }

        Console.WriteLine("PASS: " + name);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public static async Task RunAsync()
    {
        var source = new CameraSource { Channel = 1, MainStream = "rtsp://127.0.0.1/live", MotionActivated = true };
        Check(source.PollIntervalSeconds == 1 &&
            JsonSerializer.Deserialize<CameraSource>("{}")!.PollIntervalSeconds == 1 &&
            JsonSerializer.Deserialize<CameraSource>("{\"PollIntervalSeconds\":60}")!.PollIntervalSeconds == 60,
            "Existing and omitted battery poll intervals retain their whole-second values and one-second default");
        foreach (var interval in new[] { 0.1m, 0.5m, 1m, 1.6m, 60.5m, 3600m })
        {
            var candidate = source with
            {
                PollIntervalSeconds = interval
            };
            Check(candidate.Validate() is null &&
                JsonSerializer.Deserialize<CameraSource>(JsonSerializer.Serialize(candidate))!.PollIntervalSeconds == interval,
                $"Battery poll interval {interval} seconds is valid and survives portable JSON serialization");
        }
        foreach (var interval in new[] { -0.1m, 0m, 0.09m, 0.15m, 1.25m, 3600.1m })
        {
            Check((source with { PollIntervalSeconds = interval }).Validate() is not null,
                $"Battery poll interval {interval} seconds is rejected without rounding");
        }
        var calls = 0;
        var active = false;
        var availability = new BatteryCameraAvailability(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ProbeResult(Volatile.Read(ref active), active ? 200 : 404, "fixture"));
        }, TimeSpan.FromMinutes(1));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = 0;
        var first = availability.WaitAsync(_ => Interlocked.Increment(ref waiting), stop.Token);
        var second = availability.WaitAsync(_ => Interlocked.Increment(ref waiting), stop.Token);
        await Until(() => waiting >= 2);
        Check(calls == 1 && !first.IsCompleted && !second.IsCompleted, "Viewing and recording share an idle probe without launching decoders");
        active = true;
        availability.CheckNow();
        await Task.WhenAll(first, second);
        Check(calls == 2, "Check now interrupts a long poll wait and wakes both consumers with one probe");
        availability.VideoArrived();
        Check(availability.Verified, "Actual video verifies the battery stream independently of DESCRIBE");
        active = false;
        availability.StreamEnded();
        using var cancelled = new CancellationTokenSource(50);
        try
        {
            await availability.WaitAsync(_ => { }, cancelled.Token);
            throw new Exception("Cancellation not observed");
        }
        catch (OperationCanceledException) { Check(calls == 2, "Cancellation releases an idle waiter without an extra request"); }

        // No FFmpeg executable is needed while a camera reports sleep or an authentication failure.
        var error = new BatteryCameraAvailability(_ => Task.FromResult(new ProbeResult(false, 401, "RTSP login rejected")), TimeSpan.FromSeconds(1));
        var settings = new ViewerSettings { Sources = [source] };
        await using (var web = new WebCamera(settings, "", 1, "missing-ffmpeg", 1, error))
        {
            await Until(() => web.State.Status == "error");
            Check(web.State.Message.Contains("login rejected") && web.Frame is null, "Battery authentication failure remains visible instead of being labelled sleeping");
        }
        var sleeping = new BatteryCameraAvailability(_ => Task.FromResult(new ProbeResult(false, 404, "not found")), TimeSpan.FromSeconds(1));
        await using (var web = new WebCamera(settings, "", 1, "missing-ffmpeg", 1, sleeping))
        {
            await Until(() => web.State.Status == "waiting");
            Check(web.State.Message.Contains("not yet verified"), "An unverified 404 does not claim a working camera");
        }

        var media = new ControlledVideo();
        var store = new Store();
        var events = new Publisher();
        var cameraSettings = new CameraRecordingSettings { Channel = 1, RecordWhenStreaming = true };
        var recording = new RecordingSettings { Directory = Path.GetTempPath(), PreCaptureSeconds = 0, Cameras = [cameraSettings], MaximumEventSeconds = 60 };
        Check((recording with { Cameras = [cameraSettings with { StreamSchedule = [new("08:00", "08:00")] }] }).Validate() is not null,
            "Invalid stream recording schedules are rejected");
        await using (var camera = new RecordingCamera(settings, "", 1, recording, new NoDetector(), new NoFrames(), media, store, events))
        {
            await media.Started.Task;
            await media.Send(30);
            await Until(() => store.Writers.Count == 1 && store.Writers[0].Fragments == 1);
            Check(store.Triggers.Single().Source == "stream" && !camera.State.Motion, "First available fragment records without detector warmup and is labelled activity");
            await media.Send(30);
            await Until(() => store.Writers[0].Completed);
            await media.Send(1);
            await Until(() => store.Writers.Count == 2);
            Check(store.Writers[0].Fragments == 2 && !store.Writers[0].Interrupted, "Long activity splits into bounded clips without dropping the boundary fragment");
            camera.SetArming(new("disarmed", DateTimeOffset.UtcNow.AddMinutes(1)));
            await media.Send(1);
            await Until(() => store.Writers[1].Completed);
            Check(store.Writers[1].Fragments == 1, "Disarming closes the clip and does not append the next fragment");
            camera.SetArming(new("armed", DateTimeOffset.UtcNow.AddMinutes(1)));
            await media.Send(1);
            await Until(() => store.Writers.Count == 3);
            media.End();
            await Until(() => camera.State.Status == "waiting");
            Check(store.Writers.All(w => w.Completed && !w.Interrupted) && !events.Events.Any(e => e.Type is "camera.offline" or "recording.failed"),
                "Stream end finalizes cleanly without false offline or recording failure events");
        }

        media = new ControlledVideo();
        store = new Store();
        var nextDay = ((int)DateTime.UtcNow.DayOfWeek + 1) % 7;
        var scheduled = recording with
        {
            TimeZoneId = "UTC",
            Cameras = [cameraSettings with { StreamSchedule = [new("", "", [nextDay], true)] }]
        };
        await using (var camera = new RecordingCamera(settings, "", 1, scheduled, new NoDetector(), new NoFrames(), media, store))
        {
            await media.Started.Task;
            await media.Send(1);
            await Until(() => camera.State.Status == "scheduled");
            Check(store.Writers.Count == 0, "Outside the stream schedule, available video creates no recording");
            camera.SetArming(new("armed", DateTimeOffset.UtcNow.AddMinutes(1)));
            await media.Send(1);
            await Until(() => store.Writers.Count == 1);
            Check(true, "Temporary arming overrides the stream schedule");
        }
        Check(store.Writers.Single().Interrupted, "Shutdown finalizes an active clip as interrupted");

        media = new ControlledVideo();
        store = new Store { FailAppend = true };
        await using (var camera = new RecordingCamera(settings, "", 1, recording, new NoDetector(), new NoFrames(), media, store))
        {
            await media.Started.Task;
            await media.Send(1);
            await Until(() => camera.State.Status == "retrying");
            Check(camera.State.Message.Contains("Disk fixture") && store.Writers.Single().Interrupted, "Storage failures remain errors and are not mistaken for normal sleep");
        }
    }

    private sealed class ControlledVideo : IEncodedVideoSource
    {
        private readonly Channel<double> _parts = Channel.CreateUnbounded<double>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Send(double seconds) => _parts.Writer.WriteAsync(seconds).AsTask();
        public void End() => _parts.Writer.TryComplete();
        public async Task RunAsync(ViewerSettings c, string p, int n, Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            await initialized([1]);
            Started.TrySetResult();
            await foreach (var seconds in _parts.Reader.ReadAllAsync(token))
            {
                await fragment(new([1], seconds));
            }
        }
    }
    private sealed class NoFrames : IMotionFrameSource
    {
        public Task RunAsync(ViewerSettings c, string p, int n, Func<byte[], int, int, Task> frame, CancellationToken token) => throw new Exception("Unexpected motion stream");
    }
    private sealed class NoDetector : IMotionDetectorFactory
    {
        public string Name => "unused";
        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo zone) => throw new Exception("Unexpected detector");
    }
    private sealed class Publisher : ICameraEventPublisher
    {
        public ConcurrentQueue<CameraEvent> Events { get; } = new();
        public void Publish(CameraEvent value) => Events.Enqueue(value);
    }
    private sealed class Store : IRecordingEventStore
    {
        public List<Writer> Writers { get; } = [];
        public List<RecordingTrigger> Triggers { get; } = [];
        public bool FailAppend
        {
            get; init;
        }
        public Task<IEventWriter> BeginAsync(int channel, string name, byte[] init, double pre, CancellationToken token, DateTimeOffset? at = null) => throw new Exception("Expected activity trigger");
        public Task<IEventWriter> BeginTriggeredAsync(int channel, string name, byte[] init, double pre, RecordingTrigger trigger, CancellationToken token, DateTimeOffset? at = null)
        {
            var writer = new Writer(FailAppend);
            Writers.Add(writer);
            Triggers.Add(trigger);
            return Task.FromResult<IEventWriter>(writer);
        }
    }
    private sealed class Writer(bool fail) : IEventWriter
    {
        public int Fragments;
        public bool Completed, Interrupted;
        public Task AppendAsync(VideoFragment fragment, CancellationToken token)
        {
            if (fail)
            {
                throw new IOException("Disk fixture failure");
            }

            Fragments++;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(bool interrupted, CancellationToken token)
        {
            Interrupted = interrupted;
            Completed = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
