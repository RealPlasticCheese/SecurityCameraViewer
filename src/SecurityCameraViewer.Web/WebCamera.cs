using System.Diagnostics;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record CameraState(int Channel, string Name, bool Live, string Message, double? FrameAgeSeconds)
{
    public bool? HasAudio
    {
        get; init;
    }
    public string? AudioCodec
    {
        get; init;
    }
    public string Status { get; init; } = "connecting";
    public bool MotionActivated
    {
        get; init;
    }
    public DateTimeOffset? LastSeenAt
    {
        get; init;
    }
}
public sealed record CameraFrame(byte[] Jpeg, long Timestamp);

/// <summary>One shared decoder per camera, with bounded latest-frame storage for all browsers.</summary>
public sealed class WebCamera : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _shutdown;
    private readonly Task _worker;
    private readonly CameraAudioDiscovery _audio;
    private CameraState _state;
    private CameraFrame? _frame;
    private readonly BatteryCameraAvailability? _availability;
    private readonly TimeSpan _videoTimeout;
    private readonly PreviewDemand? _previewDemand;
    private readonly bool _ownsPreviewDemand;
    private Task? _disposing;
    public CameraState State
    {
        get
        {
            var audio = _audio.Result;
            return Volatile.Read(ref _state) with
            {
                HasAudio = audio?.HasAudio,
                AudioCodec = audio?.AudioCodec
            };
        }
    }
    public CameraFrame? Frame => Volatile.Read(ref _frame);

    /// <summary>The latest picture for browser playback, excluding pictures past the configured timeout.</summary>
    public CameraFrame? VideoFrame
    {
        get
        {
            var frame = Frame;
            return frame is not null && _videoTimeout > TimeSpan.Zero &&
                Stopwatch.GetElapsedTime(frame.Timestamp) >= _videoTimeout ? null : frame;
        }
    }

    public int Generation
    {
        get;
    }

    public WebCamera(ViewerSettings settings, string password, int channel, string ffmpeg, int generation,
        BatteryCameraAvailability? availability = null, ICameraConnectionRouter? connectionRouter = null,
        bool onDemand = false, PreviewDemand? previewDemand = null)
    {
        _shutdown = _stop.Token;
        _previewDemand = previewDemand ?? (onDemand ? new() : null);
        _ownsPreviewDemand = previewDemand is null && onDemand;
        _availability = availability;
        _videoTimeout = TimeSpan.FromSeconds(settings.VideoTimeoutSeconds);
        Generation = generation;
        _state = new(channel, settings.CameraName(channel), false, "Connecting to camera…", null)
        {
            MotionActivated = availability is not null
        };
        _audio = new(async token =>
        {
            ProbeResult? result;
            if (availability is not null)
            {
                result = null;
                await availability.WaitAsync(value => result = value, token);
            }
            else
            {
                result = await RtspProbe.DescribeAsync(settings with
                {
                    Substream = false
                }, password, channel, token);
            }
            return await new CameraAudioVerification(new FfmpegProcess(ffmpeg))
                .VerifyAsync(result!, settings, password, channel, token);
        });
        _worker = Task.Run(() => RunAsync(settings, password, channel, ffmpeg, connectionRouter));
    }

    public void CheckNow() => _availability?.CheckNow();

    public IDisposable SubscribePreview()
    {
        if (_stop.IsCancellationRequested)
        {
            return PreviewDemand.EmptyLease.Instance;
        }
        try
        {
            return _previewDemand?.Acquire() ?? PreviewDemand.EmptyLease.Instance;
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            return PreviewDemand.EmptyLease.Instance;
        }
    }

    /// <summary>Returns a retained picture unless refresh requires a frame decoded after this request.</summary>
    public async Task<CameraFrame?> SnapshotAsync(CancellationToken token, bool refresh = false)
    {
        var requestedAt = Stopwatch.GetTimestamp();
        token.ThrowIfCancellationRequested();
        if (!refresh && Frame is { } cached)
        {
            return cached;
        }
        if (_stop.IsCancellationRequested)
        {
            return null;
        }
        using var preview = SubscribePreview();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown);
        // Match the existing camera startup watchdog; snapshots must not wait indefinitely for offline cameras.
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Frame is { } frame && (!refresh || frame.Timestamp > requestedAt))
                {
                    return frame;
                }
                // A resumed preview can briefly retain the previous capture's failure state.
                // Refresh waits for new video up to the same startup deadline without returning a stale picture.
                if (!refresh && (State.Status is "retrying" or "error" or "waiting"))
                {
                    return null;
                }
                await Task.Delay(25, timeout.Token);
            }
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task RunAsync(ViewerSettings settings, string password, int channel, string ffmpeg, ICameraConnectionRouter? connectionRouter)
    {
        try
        {
            if (_previewDemand is null)
            {
                await CaptureAsync(settings, password, channel, ffmpeg, connectionRouter, _stop.Token);
                return;
            }
            while (!_stop.IsCancellationRequested)
            {
                Publish(false, "Preview paused", "idle");
                var demand = await _previewDemand.WaitAsync(_stop.Token);
                using var active = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, demand);
                await CaptureAsync(settings, password, channel, ffmpeg, connectionRouter, active.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            Publish(false, "Disconnected", "disconnected");
        }
    }

    private async Task CaptureAsync(ViewerSettings settings, string password, int channel, string ffmpeg,
        ICameraConnectionRouter? connectionRouter, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var recovery = new CameraRecovery(settings.RetryDelaysSeconds);
        try
        {
            while (!token.IsCancellationRequested)
            {
                string failure;
                try
                {
                    if (_availability is not null)
                    {
                        await _availability.WaitAsync(result => Publish(false,
                            result.Success ? "Activity available · connecting…" : result.StatusCode == 404 ? _availability.WaitingMessage : result.Message,
                            result.Success ? "connecting" : result.StatusCode == 404 ? "waiting" : "error"), token);
                        settings = settings with
                        {
                            Substream = false
                        };
                    }
                    recovery.StartAttempt(clock.Elapsed);
                    var attemptStarted = clock.Elapsed;
                    var connection = connectionRouter?.Resolve(settings, password, channel) ?? new CameraConnection(settings, password);
                    await using var session = new FfmpegSession(connection.Settings, connection.Password, channel, ffmpeg);
                    while (true)
                    {
                        byte[]? jpeg = null;
                        if (session.ConsumeFrame(bytes => jpeg = bytes, out var timestamp))
                        {
                            Volatile.Write(ref _frame, new(jpeg!, timestamp));
                            _availability?.VideoArrived();
                            _state = _state with
                            {
                                LastSeenAt = DateTimeOffset.UtcNow
                            };
                            recovery.FrameArrived(clock.Elapsed);
                        }
                        var batteryStalled = _availability is not null &&
                            clock.Elapsed - (recovery.HasFrameThisAttempt ? recovery.LastFrame!.Value : attemptStarted) >= TimeSpan.FromSeconds(8);
                        if (session.IsCompleted || recovery.IsStalled(clock.Elapsed) || batteryStalled)
                        {
                            failure = session.IsCompleted ? session.Failure : "Camera stopped sending video";
                            break;
                        }
                        Publish(!recovery.IsStale(clock.Elapsed), Frame is null ? "Connecting to camera…" : "Waiting for fresh video…");
                        await Task.Delay(50, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Isolate the whole camera attempt, including routing and decoder cleanup.
                    // Exception messages can contain authenticated stream URLs.
                    failure = $"Camera playback interrupted ({ex.GetType().Name})";
                }
                if (_availability is not null)
                {
                    _availability.StreamEnded();
                    var expectedEnd = recovery.HasFrameThisAttempt && failure is "Camera connection ended" or "Camera stopped sending video";
                    Publish(false, expectedEnd ? _availability.WaitingMessage : failure, expectedEnd ? "waiting" : "error");
                    // Bound unsuccessful decoder attempts even when DESCRIBE advertises video without sending it.
                    await _availability.RetryDelayAsync(token);
                    continue;
                }
                recovery.Failed(clock.Elapsed);
                while (!recovery.ShouldRetry(clock.Elapsed))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling((recovery.RetryAt!.Value - clock.Elapsed).TotalSeconds));
                    Publish(false, $"{failure}. Retrying in {seconds}s…", "retrying");
                    await Task.Delay(100, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            Publish(false, "Disconnected", "disconnected");
        }
    }

    private void Publish(bool live, string message, string status = "connecting")
    {
        var frame = Frame;
        Volatile.Write(ref _state, _state with
        {
            Live = live,
            Status = live ? "live" : status,
            Message = live ? "Live" : message,
            FrameAgeSeconds = frame is null ? null : Stopwatch.GetElapsedTime(frame.Timestamp).TotalSeconds
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_stop)
        {
            return new(_disposing ??= StopAsync());
        }
    }

    private async Task StopAsync()
    {
        await _stop.CancelAsync();
        await Task.WhenAll(_worker, _audio.DisposeAsync().AsTask());
        if (_ownsPreviewDemand)
        {
            _previewDemand!.Dispose();
        }
        _stop.Dispose();
    }
}
