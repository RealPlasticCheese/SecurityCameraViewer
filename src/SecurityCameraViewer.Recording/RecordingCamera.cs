using System.Diagnostics;
using System.Text.Json.Serialization;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed record RecordingCameraState(int Channel, string Name, string Status, bool Motion, double ChangedPercent,
    double BufferSeconds, long BufferBytes, string Message)
{
    [JsonIgnore]
    public DetectionObservation[] Detections { get; init; } = [];
    public AiAnalysisStatus? Ai
    {
        get; init;
    }
    public AdaptiveMotionDiagnostics[] AdaptiveDiagnostics { get; init; } = [];
}

/// <summary>Coordinates one camera's replaceable frame source, detector, compressed buffer and event sink.
/// It has no dependency on browser viewers or display quality. Capture policy determines when disk work starts.</summary>
public sealed partial class RecordingCamera : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private RecordingCameraState _state;
    private readonly ICameraEventPublisher _publisher;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(RecordingTrigger Trigger, int Seconds)> _triggers = new();
    private ArmingOverride _arming = new("schedule");
    private bool _offline;
    private bool _scheduleActive = true;
    private readonly BatteryCameraAvailability? _availability;
    public ArmingOverride Arming => Volatile.Read(ref _arming);
    public bool ScheduleActive => Volatile.Read(ref _scheduleActive);
    public bool CanAcceptTrigger => State.BufferSeconds > 0 || State.Status == "recording" &&
        Arming.EffectiveMode != "disarmed" && (Arming.EffectiveMode == "armed" || ScheduleActive);
    public void SetArming(ArmingOverride value) => Volatile.Write(ref _arming, value);
    public bool TryTrigger(RecordingTrigger trigger, int seconds, bool overrideSchedule)
    {
        if (_stop.IsCancellationRequested || !CanAcceptTrigger || State.Status is "retrying" or "stopped" || _triggers.Count >= 32 ||
            !overrideSchedule && (Arming.EffectiveMode == "disarmed" || !ScheduleActive))
        {
            return false;
        }

        _triggers.Enqueue((trigger, seconds));
        return true;
    }

    public RecordingCameraState State => Volatile.Read(ref _state);
    public DetectionHistory DetectionHistory { get; }

    public RecordingCamera(ViewerSettings connection, string password, int channel, RecordingSettings settings,
        IMotionDetectorFactory detectors, IMotionFrameSource frames, IEncodedVideoSource video, IRecordingEventStore events, ICameraEventPublisher? publisher = null,
        BatteryCameraAvailability? availability = null, DetectionHistoryOptions? detectionHistoryOptions = null)
    {
        DetectionHistory = new(detectionHistoryOptions);
        _availability = availability;
        _publisher = new SourceCameraEventPublisher(publisher ?? NullCameraEventPublisher.Instance, connection);
        settings = settings.WithZoneIds();
        _state = new(channel, connection.CameraName(channel), "connecting", false, 0, 0, 0,
            settings.Camera(channel).EffectiveMode == RecordingModes.Event ? "Starting motion detection and RAM buffer…" : "Starting scheduled recording…");
        _worker = Task.Run(async () =>
        {
            var failures = 0;
            while (!_stop.IsCancellationRequested)
            {
                DetectionHistory.Clear();
                try
                {
                    if (_availability is not null)
                    {
                        await _availability.WaitAsync(result => Volatile.Write(ref _state, State with
                        {
                            Status = result.Success ? "connecting" : result.StatusCode == 404 ? "waiting" : "retrying",
                            Motion = false,
                            Detections = [],
                            AdaptiveDiagnostics = [],
                            BufferBytes = 0,
                            BufferSeconds = 0,
                            Message = result.Success ? "Activity available · connecting…" : result.StatusCode == 404 ? _availability.WaitingMessage : result.Message
                        }), _stop.Token);
                    }

                    var policy = RecordingCapturePolicy.Create(settings.Camera(channel), TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId));
                    if (!policy.UsesDetection)
                    {
                        await AttemptStreamingAsync(connection, password, channel, settings, video, events);
                    }
                    else
                    {
                        await AttemptAsync(connection, password, channel, settings, detectors, frames, video, events);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DetectionHistory.Clear();
                    var message = ex is IOException ? ex.Message : $"Recording interrupted ({ex.GetType().Name}).";
                    Volatile.Write(ref _state, State with
                    {
                        Status = "retrying",
                        Detections = [],
                        AdaptiveDiagnostics = [],
                        Motion = false,
                        BufferBytes = 0,
                        BufferSeconds = 0,
                        Message = message + " Retrying…"
                    });
                }
                try
                {
                    _availability?.StreamEnded();
                    if (_availability is not null)
                    {
                        await _availability.RetryDelayAsync(_stop.Token);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(new[] { 2, 5, 10, 20, 30 }[Math.Min(failures++, 4)]), _stop.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            DetectionHistory.Clear();
            Volatile.Write(ref _state, State with
            {
                Status = "stopped",
                Detections = [],
                AdaptiveDiagnostics = [],
                Motion = false,
                BufferBytes = 0,
                BufferSeconds = 0,
                Message = "Recording stopped."
            });
        });
    }

    private async Task AttemptAsync(ViewerSettings connection, string password, int channel, RecordingSettings settings,
        IMotionDetectorFactory detectors, IMotionFrameSource frames, IEncodedVideoSource video, IRecordingEventStore events)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using var gate = new SemaphoreSlim(1, 1);
        var clock = Stopwatch.StartNew();
        var camera = settings.Camera(channel);
        var policy = RecordingCapturePolicy.Create(camera, TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId));
        var actions = new MotionActionRouter(settings, camera);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var plan = DetectionTemplateResolver.Resolve(settings, camera);
        var detector = plan.Create(detectors, timeZone);
        var schedule = new MotionRegionMask(camera.Zones, timeZone);
        var detectorMode = "schedule";
        var activeMotion = new Dictionary<string, EventZone>();
        var online = false;
        var expectedSleep = false;
        RecordingTrigger trigger = new();
        var pendingTriggers = new List<RecordingTrigger>();
        double externalUntil = double.NegativeInfinity;
        // A fragment arrives after its next keyframe. Keep up to one extra (bounded) 30-second GOP.
        var buffer = new VideoBuffer(settings.PreCaptureSeconds + 30, settings.BufferMegabytesPerCamera * 1024L * 1024);
        byte[]? initialization = null;
        IEventWriter? writer = null;
        double lastMotion = double.NegativeInfinity;
        double eventSeconds = 0;
        double pendingAt = 0;
        var pending = false;
        long lastVideo = Stopwatch.GetTimestamp();
        long lastFrame = lastVideo;
        var motionTask = frames.RunAsync(connection with
        {
            Substream = _availability is null && camera.DetectionSubstream
        }, password, channel, plan.SourceSettings, async frame =>
        {
            Interlocked.Exchange(ref lastFrame, Stopwatch.GetTimestamp());
            var mode = Arming.EffectiveMode;
            if (mode != detectorMode)
            {
                detectorMode = mode;
                (detector as IDisposable)?.Dispose();
                detector = DetectionTemplateResolver.Resolve(settings, mode == "armed" ? camera with
                {
                    Zones = [.. camera.Zones.Select(z => z with { Times = null })]
                } : camera).Create(detectors, timeZone);
            }
            schedule.Update(frame.Width, frame.Height, frame.CapturedAt);
            var sample = mode == "disarmed" && detector is IAsyncMotionDetector
                ? new MotionSample(false, 0, false, schedule.Pixels.Any(included => included))
                : await detector.AnalyseAsync(frame, clock.Elapsed.TotalSeconds, attempt.Token);
            await gate.WaitAsync(attempt.Token);
            try
            {
                Volatile.Write(ref _scheduleActive, policy.UsesEventBuffer ? sample.ScheduleActive :
                    policy.IsAllowed(frame.CapturedAt, new("schedule")));
                var detected = sample.Detected && mode != "disarmed";
                var detectedZones = detected ? sample.Zones?.Where(z => z.Detected).Select(z => new EventZone(z.Id, z.Name)).ToArray() ?? [] : [];
                var nextMotion = detectedZones.ToDictionary(z => z.Id);
                if (detected && nextMotion.Count == 0)
                {
                    nextMotion["full-frame"] = new("full-frame", "Full frame");
                }

                foreach (var ended in activeMotion.Values.Where(z => !nextMotion.ContainsKey(z.Id)))
                {
                    _publisher.Publish(new("motion.ended", channel, State.Name, [ended], writer?.Id));
                }

                var startedZones = nextMotion.Values.Where(z => !activeMotion.ContainsKey(z.Id)).ToArray();
                foreach (var started in startedZones)
                {
                    _publisher.Publish(new("motion.started", channel, State.Name, [started], writer?.Id, new
                    {
                        sample.ChangedPercent
                    })
                    {
                        ActionIds = [.. actions.ForZone(started.Id)
                            .Where(action => action.Type == MotionAction.WebhookType)
                            .Select(action => action.Id)]
                    });
                }

                var recordingStarts = policy.UsesEventBuffer ? startedZones.Where(zone => actions.Records(zone.Id)).ToArray() : startedZones;
                if (recordingStarts.Length > 0 && writer is not null)
                {
                    await writer.AddTriggerAsync(new(Zones: recordingStarts), attempt.Token);
                }
                else if (recordingStarts.Length > 0 && pending && pendingTriggers.Count < 32)
                {
                    pendingTriggers.Add(new(Zones: recordingStarts));
                }

                activeMotion = nextMotion;
                var recordingZones = nextMotion.Values.Where(zone => actions.Records(zone.Id)).ToArray();
                if (policy.UsesEventBuffer && recordingZones.Length > 0)
                {
                    lastMotion = clock.Elapsed.TotalSeconds;
                    if (writer is null && !pending)
                    {
                        pending = true;
                        pendingAt = lastMotion;
                        trigger = new(Zones: recordingZones);
                    }
                }
                Volatile.Write(ref _state, State with
                {
                    Motion = detected,
                    Ai = sample.Ai,
                    AdaptiveDiagnostics = mode == "disarmed" ? [] : sample.AdaptiveDiagnostics,
                    Detections = mode == "disarmed" ? [] : sample.Detections ?? [],
                    ChangedPercent = sample.ChangedPercent,
                    Status = writer is not null ? "recording"
                        : mode == "disarmed" ? "disarmed"
                        : !(policy.UsesEventBuffer ? sample.ScheduleActive : policy.IsAllowed(frame.CapturedAt, Arming)) ? "scheduled"
                        : sample.Ai?.Failed == true ? "analysis-error"
                        : sample.WarmingUp ? "warming" : "armed",
                    Message = !policy.UsesEventBuffer ? writer is not null ? "Continuous recording with detection"
                        : mode == "disarmed" ? "Temporarily disarmed"
                        : !policy.IsAllowed(frame.CapturedAt, Arming) ? "Outside the recording schedule"
                        : "Waiting for main-stream keyframes…"
                        : writer is not null ? "Saving event"
                        : mode == "disarmed" ? "Temporarily disarmed · buffering in RAM"
                        : !sample.ScheduleActive ? "No detection area is active at this time."
                        : initialization is null || buffer.Seconds == 0 ? "Waiting for main-stream keyframes…"
                        : buffer.Seconds < settings.PreCaptureSeconds ? "Filling RAM buffer; pre-event footage may be shorter."
                        : sample.Ai?.Failed == true ? sample.Ai.Message
                        : "Armed · buffering in RAM"
                });
                DetectionHistory.Add(frame.CapturedAt, State.Detections, DateTimeOffset.UtcNow);
            }
            finally
            {
                gate.Release();
            }
        }, attempt.Token);
        var videoTask = video.RunAsync(connection, password, channel, bytes =>
        {
            initialization = bytes;
            return Task.CompletedTask;
        }, async fragment =>
        {
            Interlocked.Exchange(ref lastVideo, Stopwatch.GetTimestamp());
            await gate.WaitAsync(attempt.Token);
            try
            {
                if (policy.UsesEventBuffer)
                {
                    buffer.Add(fragment);
                }
                _availability?.VideoArrived();
                var now = clock.Elapsed.TotalSeconds;
                if (!online)
                {
                    online = true;
                    _offline = false;
                    _publisher.Publish(new("camera.online", channel, State.Name));
                }
                if (!policy.UsesEventBuffer)
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var allowed = policy.IsAllowed(timestamp, Arming);
                    Volatile.Write(ref _scheduleActive, policy.IsAllowed(timestamp, new("schedule")));
                    if (!allowed && writer is not null)
                    {
                        await writer.CompleteAsync(false, attempt.Token);
                        await writer.DisposeAsync();
                        writer = null;
                    }
                    if (allowed && writer is null && initialization is not null)
                    {
                        var startedAt = timestamp.AddSeconds(-fragment.DurationSeconds);
                        writer = await events.BeginTriggeredAsync(channel, connection.CameraName(channel), initialization, 0,
                            policy.AutomaticTrigger with
                            {
                                Timestamp = startedAt
                            }, attempt.Token, startedAt);
                        eventSeconds = 0;
                        if (activeMotion.Count > 0)
                        {
                            await writer.AddTriggerAsync(new(Zones: [.. activeMotion.Values]), attempt.Token);
                        }
                    }
                }
                while (_triggers.TryDequeue(out var external))
                {
                    externalUntil = Math.Max(externalUntil, now + external.Seconds);
                    if (writer is not null)
                    {
                        await writer.AddTriggerAsync(external.Trigger, attempt.Token);
                    }
                    else if (policy.UsesEventBuffer && !pending)
                    {
                        pending = true;
                        pendingAt = now;
                        trigger = external.Trigger;
                    }
                    else if (policy.UsesEventBuffer)
                    {
                        pendingTriggers.Add(external.Trigger);
                    }
                }
                if (policy.UsesEventBuffer && writer is null && pending && initialization is not null)
                {
                    trigger = trigger with
                    {
                        Zones = [.. (trigger.Zones ?? [])
                        .Concat(activeMotion.Values.Where(zone => actions.Records(zone.Id)))
                        .DistinctBy(zone => zone.Id)]
                    };
                    var snapshot = buffer.Snapshot();
                    var selected = new List<VideoFragment>();
                    double duration = 0;
                    foreach (var previous in snapshot.Reverse())
                    {
                        selected.Add(previous);
                        duration += previous.DurationSeconds;
                        if (duration >= settings.PreCaptureSeconds + now - pendingAt)
                        {
                            break;
                        }
                    }
                    writer = await events.BeginTriggeredAsync(channel, connection.CameraName(channel), initialization, duration, trigger, attempt.Token,
                        DateTimeOffset.UtcNow.AddSeconds(pendingAt - now));
                    foreach (var extra in pendingTriggers)
                    {
                        await writer.AddTriggerAsync(extra, attempt.Token);
                    }

                    pendingTriggers.Clear();
                    eventSeconds = duration;
                    pending = false;
                    foreach (var previous in selected.AsEnumerable().Reverse())
                    {
                        await writer.AppendAsync(previous, attempt.Token);
                    }
                }
                else if (writer is not null)
                {
                    await writer.AppendAsync(fragment, attempt.Token);
                    eventSeconds += fragment.DurationSeconds;
                }
                if (writer is not null && (policy.UsesEventBuffer && now - lastMotion >= settings.PostCaptureSeconds && now >= externalUntil || eventSeconds >= settings.MaximumEventSeconds))
                {
                    await writer.CompleteAsync(false, attempt.Token);
                    await writer.DisposeAsync();
                    writer = null;
                    // Split long activity into bounded clips, retaining overlap rather than leaving a gap.
                    if (policy.UsesEventBuffer && (now - lastMotion < settings.PostCaptureSeconds || now < externalUntil))
                    {
                        pending = true;
                        pendingAt = now;
                    }
                }
                Volatile.Write(ref _state, State with
                {
                    BufferBytes = buffer.Bytes,
                    BufferSeconds = buffer.Seconds,
                    Status = writer is not null ? "recording" : State.Status == "recording" ? "armed" : State.Status
                });
            }
            finally
            {
                gate.Release();
            }
        }, attempt.Token);
        var watchdog = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000, attempt.Token);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastVideo)).TotalSeconds > 35 || Stopwatch.GetElapsedTime(Interlocked.Read(ref lastFrame)).TotalSeconds > 30)
                {
                    throw new IOException("No fresh detection frames or recording keyframes. Check the stream and keyframe interval.");
                }
            }
        }, attempt.Token);
        try
        {
            var ended = await Task.WhenAny(motionTask, videoTask, watchdog);
            try
            {
                await ended;
            }
            catch (VideoSourceException) when (_availability is not null && online) { }
            if (_availability is not null && online)
            {
                expectedSleep = true;
            }
            else
            {
                throw new IOException("Camera stream ended.");
            }
        }
        finally
        {
            await attempt.CancelAsync();
            try
            {
                await Task.WhenAll(motionTask, videoTask, watchdog);
            }
            catch (Exception)
            {
                /* Observed below via the first failed worker. */
            }
            (detector as IDisposable)?.Dispose();
            DetectionHistory.Clear();
            foreach (var ended in activeMotion.Values)
            {
                _publisher.Publish(new("motion.ended", channel, State.Name, [ended], writer?.Id));
            }
            // A planned settings change is not a camera fault.
            if (_availability is null && !_stop.IsCancellationRequested && !_offline)
            {
                _offline = true;
                _publisher.Publish(new("camera.offline", channel, State.Name));
            }
            while (_triggers.TryDequeue(out var lost))
            {
                _publisher.Publish(new("recording.failed", channel, State.Name, Data: new
                {
                    trigger = lost.Trigger,
                    reason = "Camera stream interrupted before capture started"
                }));
            }

            if (pending && !_stop.IsCancellationRequested && !expectedSleep)
            {
                _publisher.Publish(new("recording.failed", channel, State.Name, Data: new
                {
                    trigger,
                    reason = "Camera stream interrupted before capture started"
                }));
            }

            if (writer is not null)
            {
                try
                {
                    await writer.CompleteAsync(!expectedSleep, CancellationToken.None);
                }
                finally
                {
                    await writer.DisposeAsync();
                }
            }
        }
        if (expectedSleep)
        {
            Volatile.Write(ref _state, State with
            {
                Status = "waiting",
                Motion = false,
                Detections = [],
                AdaptiveDiagnostics = [],
                BufferSeconds = 0,
                BufferBytes = 0,
                Message = "Waiting for activity"
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _worker;
        _stop.Dispose();
    }
}
