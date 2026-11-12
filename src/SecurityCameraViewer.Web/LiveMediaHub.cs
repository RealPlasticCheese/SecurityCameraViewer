using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record LiveMediaPacket(string Type, byte[]? Bytes = null, string? MimeType = null,
    bool HasAudio = false, string? Status = null, string? Message = null, bool StartsWithKeyframe = true)
{
    public long Timestamp { get; } = Stopwatch.GetTimestamp();
    public string? StreamId { get; init; }
    public double? StartSeconds { get; init; }
    public double? EndSeconds { get; init; }
    public DateTimeOffset? ProducedAt { get; init; }
    public string? Reason { get; init; }
    public LiveMediaQueueDiagnostics? Queue { get; init; }
}

public sealed record LiveMediaQueueDiagnostics(int QueuedPackets, int QueuedBytes, int IncomingBytes,
    int MaximumQueuedPackets, int MaximumQueuedBytes);

/// <summary>A bounded listener queue. Slow listeners disconnect instead of losing MP4 fragments.</summary>
public sealed class LiveMediaSubscription : IAsyncDisposable
{
    public const string QueueOverflowMessage = "Live playback fell behind. Reconnecting…";
    public const int MaximumQueuedBytes = 4 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Channel<LiveMediaPacket> _packets;
    private readonly int _maximumQueuedBytes;
    private readonly int _maximumQueuedPackets;
    private LiveMediaQueueDiagnostics? _overflowQueue;
    private readonly Func<Task> _release;
    private int _queuedBytes;
    private bool _closed;
    private int _disposed;
    private bool _waitingForKeyframe = true;

    internal LiveMediaSubscription(Func<Task> release, LiveMediaOptions? options = null)
    {
        var budgets = options ?? new();
        budgets.Validate();
        _packets = Channel.CreateBounded<LiveMediaPacket>(budgets.MaximumQueuedPackets);
        _maximumQueuedBytes = budgets.MaximumQueuedBytes;
        _maximumQueuedPackets = budgets.MaximumQueuedPackets;
        _release = release;
    }

    internal void Publish(LiveMediaPacket packet)
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            if (packet.Type is "init" or "state")
            {
                _waitingForKeyframe = true;
            }
            if (packet.Type == "fragment")
            {
                if (_waitingForKeyframe && !packet.StartsWithKeyframe)
                {
                    return;
                }
                _waitingForKeyframe = false;
            }

            var length = packet.Bytes?.Length ?? 0;
            if ((long)_queuedBytes + length > _maximumQueuedBytes || !_packets.Writer.TryWrite(packet))
            {
                _overflowQueue = new(_packets.Reader.Count, _queuedBytes, length, _maximumQueuedPackets, _maximumQueuedBytes);
                CloseCore("retrying", QueueOverflowMessage);
                return;
            }

            _queuedBytes += length;
        }
    }

    public LiveMediaQueueDiagnostics QueueDiagnostics
    {
        get
        {
            lock (_sync)
            {
                return _overflowQueue ?? new(_packets.Reader.Count, _queuedBytes, 0, _maximumQueuedPackets, _maximumQueuedBytes);
            }
        }
    }

    internal void Close(string status, string message)
    {
        lock (_sync)
        {
            CloseCore(status, message);
        }
    }

    private void CloseCore(string status, string message)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        while (_packets.Reader.TryRead(out _))
        {
        }

        _queuedBytes = 0;
        _packets.Writer.TryWrite(new("state", Status: status, Message: message)
        {
            Reason = _overflowQueue is null ? null : "queue-overflow",
            Queue = _overflowQueue
        });
        _packets.Writer.TryComplete();
    }

    public async IAsyncEnumerable<LiveMediaPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken token)
    {
        while (await _packets.Reader.WaitToReadAsync(token))
        {
            LiveMediaPacket? packet;
            lock (_sync)
            {
                if (!_packets.Reader.TryRead(out packet))
                {
                    continue;
                }

                _queuedBytes -= packet.Bytes?.Length ?? 0;
            }

            yield return packet;
        }
    }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
        ? new(_release()) : ValueTask.CompletedTask;
}

/// <summary>Owns one live media process per camera, shared by listeners and optional rewind capture.</summary>
public sealed class LiveMediaHub(ILiveMediaSource source, LiveMediaOptions? options = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Session> _sessions = [];

    public bool IsLive(int channel)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(channel, out var session) && session.IsLive;
        }
    }

    public int SessionCount
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Count;
            }
        }
    }

    public LiveMediaSubscription Subscribe(ViewerSettings settings, string password, int channel,
        BatteryCameraAvailability? availability)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(channel, out var session))
            {
                session = new(source, settings, password, channel, availability);
                _sessions.Add(channel, session);
            }

            if (session.ListenerCount >= 16)
            {
                throw new InvalidOperationException("Too many live listeners for this camera.");
            }

            LiveMediaSubscription? subscription = null;
            subscription = new(() => ReleaseAsync(channel, session, subscription!), options);
            session.Add(subscription);
            return subscription;
        }
    }

    public async Task ConfigureRewindAsync(ViewerSettings settings, string password, LiveRewindSettings rewind,
        Func<int, BatteryCameraAvailability?> availability)
    {
        if (rewind.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        var stopping = new List<Session>();
        lock (_sync)
        {
            if (rewind.Enabled)
            {
                foreach (var channel in settings.Channels)
                {
                    if (!_sessions.ContainsKey(channel))
                    {
                        _sessions.Add(channel, new(source, settings, password, channel, availability(channel)));
                    }
                }
            }

            foreach (var (channel, session) in _sessions.ToArray())
            {
                session.ConfigureRewind(rewind);
                if (!session.Retained && session.ListenerCount == 0)
                {
                    _sessions.Remove(channel);
                    stopping.Add(session);
                }
            }
        }

        await Task.WhenAll(stopping.Select(session => session.StopAsync()));
    }

    public RewindCamera[] RewindSnapshot()
    {
        lock (_sync)
        {
            return _sessions.Values.Select(session => session.RewindSnapshot()).OfType<RewindCamera>().ToArray();
        }
    }

    public LiveRewindBuffer? RewindBuffer(int channel)
    {
        lock (_sync)
        {
            return _sessions.GetValueOrDefault(channel)?.RewindBuffer;
        }
    }

    private async Task ReleaseAsync(int channel, Session session, LiveMediaSubscription subscription)
    {
        var stop = false;
        lock (_sync)
        {
            session.Remove(subscription);
            if (session.ListenerCount == 0 && !session.Retained)
            {
                if (_sessions.GetValueOrDefault(channel) == session)
                {
                    _sessions.Remove(channel);
                }

                stop = true;
            }
        }

        if (stop)
        {
            await session.StopAsync();
        }
    }

    public async Task StopAsync()
    {
        Session[] sessions;
        lock (_sync)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        await Task.WhenAll(sessions.Select(session => session.StopAsync()));
    }

    private sealed class Session(ILiveMediaSource source, ViewerSettings settings, string password,
        int channel, BatteryCameraAvailability? availability)
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<LiveMediaSubscription> _listeners = [];
        private Task? _worker;
        private Task? _stopping;
        private LiveMediaPacket _state = new("state", Status: "connecting", Message: "Connecting live sound…");
        private LiveMediaPacket? _initialization;
        private LiveMediaPacket? _latest;
        private LiveRewindBuffer? _rewind;
        private int _rewindSeconds;
        private long _rewindMegabytes;
        public LiveRewindBuffer? RewindBuffer
        {
            get
            {
                lock (_sync)
                {
                    return _rewind;
                }
            }
        }
        public bool Retained => RewindBuffer is not null;

        public bool IsLive
        {
            get
            {
                lock (_sync)
                {
                    return _latest is not null && Stopwatch.GetElapsedTime(_latest.Timestamp).TotalSeconds < 3;
                }
            }
        }

        public void ConfigureRewind(LiveRewindSettings rewind)
        {
            lock (_sync)
            {
                if (!rewind.Enabled)
                {
                    _rewind = null;
                    return;
                }

                if (_rewind is null || _rewindSeconds != rewind.Seconds || _rewindMegabytes != rewind.MegabytesPerCamera)
                {
                    _rewindSeconds = rewind.Seconds;
                    _rewindMegabytes = rewind.MegabytesPerCamera;
                    _rewind = new(TimeSpan.FromSeconds(rewind.Seconds), rewind.MegabytesPerCamera * 1024 * 1024);
                    if (_initialization is { Bytes: { } bytes, MimeType: { } mimeType })
                    {
                        _rewind.Initialize(new(bytes, mimeType, _initialization.HasAudio));
                    }
                }

                _worker ??= Task.Run(RunAsync);
            }
        }

        public RewindCamera? RewindSnapshot()
        {
            lock (_sync)
            {
                return _rewind?.Snapshot(channel, _latest is not null && Stopwatch.GetElapsedTime(_latest.Timestamp).TotalSeconds < 3
                    ? "live" : _state.Status ?? "connecting", _state.Message ?? "Connecting buffered video…");
            }
        }
        public int ListenerCount
        {
            get
            {
                lock (_sync)
                {
                    return _listeners.Count;
                }
            }
        }

        public void Add(LiveMediaSubscription listener)
        {
            lock (_sync)
            {
                _listeners.Add(listener);
                listener.Publish(_state);
                if (_initialization is not null)
                {
                    listener.Publish(_initialization);
                    if (_latest is not null && Stopwatch.GetElapsedTime(_latest.Timestamp).TotalSeconds < 3)
                    {
                        listener.Publish(_latest);
                    }
                }

                _worker ??= Task.Run(RunAsync);
            }
        }

        public void Remove(LiveMediaSubscription listener)
        {
            lock (_sync)
            {
                _listeners.Remove(listener);
            }

            listener.Close("disconnected", "Listening stopped.");
        }

        private void PublishState(string status, string message)
        {
            lock (_sync)
            {
                if (_initialization is null && _state.Status == status && _state.Message == message)
                {
                    return;
                }

                _initialization = null;
                _latest = null;
                _state = new("state", Status: status, Message: message);
                foreach (var listener in _listeners)
                {
                    listener.Publish(_state);
                }
            }
        }

        private async Task RunAsync()
        {
            var clock = Stopwatch.StartNew();
            var recovery = new CameraRecovery(settings.RetryDelaysSeconds);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var received = false;
                    try
                    {
                        if (availability is not null)
                        {
                            await availability.WaitAsync(result => PublishState(
                                result.Success ? "connecting" : result.StatusCode == 404 ? "waiting" : "error",
                                result.Success ? "Activity available · connecting sound…" : result.StatusCode == 404 ?
                                    availability.WaitingMessage : result.Message), _stop.Token);
                        }

                        recovery.StartAttempt(clock.Elapsed);
                        await CaptureAsync(() =>
                        {
                            received = true;
                            recovery.FrameArrived(clock.Elapsed);
                        });
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (UnsupportedCameraVideoException)
                    {
                        PublishState("unsupported", "This camera stream needs server JPEG previews for compatibility.");
                        await Task.Delay(Timeout.InfiniteTimeSpan, _stop.Token);
                    }
                    catch (Exception)
                    {
                        // Contain synchronous source failures, malformed media and cleanup errors.
                        // FFmpeg diagnostics may contain credentials. Publish only fixed messages.
                    }

                    availability?.StreamEnded();
                    PublishState(availability is not null && received ? "waiting" : "retrying",
                        availability is not null && received ? "Waiting for camera activity." : "Live sound unavailable. Retrying…");
                    if (availability is null)
                    {
                        recovery.Failed(clock.Elapsed);
                        await recovery.WaitForRetryAsync(() => clock.Elapsed, _stop.Token);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                lock (_sync)
                {
                    foreach (var listener in _listeners)
                    {
                        listener.Close("disconnected", "Listening stopped.");
                    }
                }
            }
        }

        private async Task CaptureAsync(Action received)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var lastFragment = Stopwatch.GetTimestamp();
            var capture = source.RunAsync(settings, password, channel, initialization =>
            {
                lock (_sync)
                {
                    _initialization = new("init", initialization.Bytes, initialization.MimeType, initialization.HasAudio)
                    {
                        StreamId = Guid.NewGuid().ToString("N")
                    };
                    _latest = null;
                    _rewind?.Initialize(initialization);
                    foreach (var listener in _listeners)
                    {
                        listener.Publish(_initialization);
                    }
                }

                return Task.CompletedTask;
            }, fragment =>
            {
                Interlocked.Exchange(ref lastFragment, Stopwatch.GetTimestamp());
                received();
                availability?.VideoArrived();
                lock (_sync)
                {
                    _rewind?.Add(fragment);
                    _latest = new("fragment", fragment.Bytes, StartsWithKeyframe: fragment.StartsWithKeyframe)
                    {
                        StreamId = _initialization?.StreamId,
                        StartSeconds = fragment.StartSeconds,
                        EndSeconds = fragment.PresentationEndSeconds ?? fragment.StartSeconds + fragment.DurationSeconds,
                        ProducedAt = fragment.ProducedAt
                    };
                    foreach (var listener in _listeners)
                    {
                        listener.Publish(_latest);
                    }
                }

                return Task.CompletedTask;
            }, attempt.Token);
            try
            {
                while (!capture.IsCompleted && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastFragment)).TotalSeconds < 8)
                {
                    await Task.WhenAny(capture, Task.Delay(250, _stop.Token));
                    _stop.Token.ThrowIfCancellationRequested();
                }

                if (!capture.IsCompleted)
                {
                    await attempt.CancelAsync();
                }

                await capture;
            }
            finally
            {
                await attempt.CancelAsync();
                try
                {
                    await capture;
                }
                catch (Exception)
                {
                    // The original failure is observed above; always finish this attempt.
                }
            }
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                return _stopping ??= StopCoreAsync();
            }
        }

        private async Task StopCoreAsync()
        {
            await _stop.CancelAsync();
            if (_worker is not null)
            {
                await _worker;
            }

            _stop.Dispose();
        }
    }
}
