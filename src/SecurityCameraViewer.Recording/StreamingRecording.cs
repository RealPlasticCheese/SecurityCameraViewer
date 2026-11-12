using System.Diagnostics;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

public sealed partial class RecordingCamera
{
    // Stream activity is a recording trigger, not evidence of motion. No detector warmup or extra RTSP detection session.
    private async Task AttemptStreamingAsync(ViewerSettings connection, string password, int channel,
        RecordingSettings settings, IEncodedVideoSource video, IRecordingEventStore events)
    {
        var camera = settings.Camera(channel);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var policy = RecordingCapturePolicy.Create(camera, zone);
        var continuous = camera.EffectiveMode == RecordingModes.Continuous;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        byte[]? initialization = null;
        IEventWriter? writer = null;
        var seconds = 0d;
        var received = false;
        var expectedEnd = false;
        long lastVideo = Stopwatch.GetTimestamp();
        var task = video.RunAsync(connection with
        {
            Substream = false
        }, password, channel,
            bytes => { initialization = bytes; return Task.CompletedTask; }, async fragment =>
            {
                Interlocked.Exchange(ref lastVideo, Stopwatch.GetTimestamp());
                if (!received && continuous)
                {
                    _offline = false;
                    _publisher.Publish(new("camera.online", channel, State.Name));
                }
                received = true;
                _availability?.VideoArrived();
                var timestamp = DateTimeOffset.UtcNow;
                var scheduled = policy.IsAllowed(timestamp, new("schedule"));
                Volatile.Write(ref _scheduleActive, scheduled);
                var mode = Arming.EffectiveMode;
                var allowed = policy.IsAllowed(timestamp, Arming);
                if (!allowed && writer is not null)
                {
                    await writer.CompleteAsync(false, attempt.Token);
                    await writer.DisposeAsync();
                    writer = null;
                }
                if (allowed && initialization is not null)
                {
                    if (writer is null)
                    {
                        // This first complete fragment is the start of available video, not pre-event footage.
                        var startedAt = timestamp.AddSeconds(-fragment.DurationSeconds);
                        writer = await events.BeginTriggeredAsync(channel, connection.CameraName(channel), initialization, 0,
                            policy.AutomaticTrigger with
                            {
                                Timestamp = startedAt
                            }, attempt.Token, startedAt);
                        seconds = 0;
                    }
                    while (_triggers.TryDequeue(out var external))
                    {
                        await writer.AddTriggerAsync(external.Trigger, attempt.Token);
                    }

                    await writer.AppendAsync(fragment, attempt.Token);
                    seconds += fragment.DurationSeconds;
                    if (seconds >= settings.MaximumEventSeconds)
                    {
                        await writer.CompleteAsync(false, attempt.Token);
                        await writer.DisposeAsync();
                        writer = null;
                    }
                }
                Volatile.Write(ref _state, State with
                {
                    Status = allowed ? "recording" : mode == "disarmed" ? "disarmed" : "scheduled",
                    Motion = false,
                    BufferBytes = 0,
                    BufferSeconds = 0,
                    Message = allowed ? continuous ? "Continuous recording" : "Recording available video" : mode == "disarmed" ? "Temporarily disarmed" : "Outside the recording schedule"
                });
            }, attempt.Token);
        try
        {
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(500, _stop.Token));
                _stop.Token.ThrowIfCancellationRequested();
                // Allows up to a 30-second keyframe interval; no unbounded FFmpeg wait after sleep.
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastVideo)).TotalSeconds > 35)
                {
                    expectedEnd = received && (!continuous || _availability is not null);
                    await attempt.CancelAsync();
                    break;
                }
            }
            try
            {
                await task;
                expectedEnd = received && (!continuous || _availability is not null);
            }
            catch (VideoSourceException) when (received && (!continuous || _availability is not null)) { expectedEnd = true; }
            catch (OperationCanceledException) when (expectedEnd && !_stop.IsCancellationRequested) { }
            if (!received)
            {
                throw new IOException("The camera advertised video but sent no complete recording fragments.");
            }
            if (continuous && !expectedEnd && !_stop.IsCancellationRequested)
            {
                throw new IOException("Camera stream ended. Continuous recording will reconnect.");
            }
        }
        finally
        {
            await attempt.CancelAsync();
            try
            {
                await task;
            }
            catch (Exception) { /* Observed above; ensure callbacks stop before closing the writer. */ }
            if (writer is not null)
            {
                try
                {
                    await writer.CompleteAsync(!expectedEnd || _stop.IsCancellationRequested, CancellationToken.None);
                }
                finally { await writer.DisposeAsync(); }
            }
            if (continuous && _availability is null && !_stop.IsCancellationRequested && !_offline)
            {
                _offline = true;
                _publisher.Publish(new("camera.offline", channel, State.Name));
            }
        }
        Volatile.Write(ref _state, State with
        {
            Status = "waiting",
            Motion = false,
            BufferBytes = 0,
            BufferSeconds = 0,
            Message = "Waiting for activity"
        });
    }
}
