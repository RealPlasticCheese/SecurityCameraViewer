namespace SecurityCameraViewer.Recording;

public sealed record RecordingStorageState(string? ActiveDirectory = null, DateTimeOffset? LastFailoverAt = null,
    string? PreviousDirectory = null, DateTimeOffset? CopiedThroughAtFailover = null);

public sealed record RecordingLocationStatus(string Directory, bool Active, string Status, DateTimeOffset? AvailableThrough,
    double? LagSeconds, bool CanActivate, long? FreeBytes);

/// <summary>Routes capture to one archive and runs independent health, finalization and copy jobs for each destination.</summary>
public sealed class RecordingStoragePool : IRecordingEventStore, IDisposable
{
    private const int BatchSize = 32;
    private readonly object _gate = new();
    private readonly Location[] _locations;
    private readonly RecordingSettings _settings;
    private readonly FfmpegProcess _ffmpeg;
    private readonly ICameraEventPublisher _publisher;
    private readonly TimeSpan _ioTimeout;
    private readonly Action<RecordingStorageState> _persist;
    private readonly Func<string, bool>? _canOpen;
    private readonly CancellationTokenSource _stop = new();
    private Location? _active;
    private RecordingStorageState _state;
    private RecordingStorageState _persistedState;
    private Task? _persistence;
    private string _notice = "";

    public RecordingStoragePool(RecordingSettings settings, FfmpegProcess ffmpeg, ICameraEventPublisher publisher,
        TimeSpan ioTimeout, RecordingStorageState state, Action<RecordingStorageState> persist,
        Func<string, bool>? canOpen = null)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _publisher = publisher;
        _ioTimeout = ioTimeout;
        _state = state;
        _persistedState = state;
        _persist = persist;
        _canOpen = canOpen;
        _locations = [new(new(settings.Directory, settings.MaximumStorageGigabytes, settings.PrimaryLocationId)),
            .. settings.Mirrors.Select(mirror => new Location(mirror))];

        if (_locations.Length == 1 && settings.PrimaryLocationId is null && canOpen?.Invoke(settings.Directory) != false)
        {
            _locations[0].Store = Open(_locations[0]);
            _locations[0].Library = _locations[0].Store;
            _locations[0].Status = "ready";
            _active = _locations[0];
        }
    }

    public FileRecordingStore? ActiveStore
    {
        get { lock (_gate) { return _active?.Store; } }
    }

    public FileRecordingStore? ReadableStore
    {
        get
        {
            lock (_gate)
            {
                return _active?.Store ?? _locations.FirstOrDefault(location => location.Status == "full")?.Library;
            }
        }
    }

    public string Notice
    {
        get { lock (_gate) { return _notice; } }
    }

    public RecordingStorageState State
    {
        get { lock (_gate) { return _state; } }
    }

    public RecordingLocationStatus[] Snapshot()
    {
        lock (_gate)
        {
            return _locations.Select(location => new RecordingLocationStatus(location.Settings.Directory,
                location == _active, location.Status == "ready" && location != _active && !location.Synchronized
                    ? location.CopyStatus ?? "catching-up" : location.Status,
                location.Through,
                _active?.Through is { } latest && location.Through is { } through
                    ? Math.Max(0, (latest - through).TotalSeconds) : null,
                location != _active && location.Status == "ready" && location.Synchronized, location.FreeBytes)).ToArray();
        }
    }

    public void Poll()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            foreach (var location in _locations)
            {
                if (_canOpen?.Invoke(location.Settings.Directory) == false)
                {
                    Fail(location, "stalled");
                    continue;
                }
                if (location.Health is { IsCompleted: false })
                {
                    if (DateTimeOffset.UtcNow - location.HealthStarted >= _ioTimeout)
                    {
                        Fail(location, "stalled");
                    }
                    continue;
                }

                if (location.Store is null && (location.Operations > 0 ||
                    location.Maintenance is { IsCompleted: false } || location.Copy is { IsCompleted: false }))
                {
                    continue;
                }
                location.HealthStarted = DateTimeOffset.UtcNow;
                var previous = location.Store;
                location.Health = Task.Run(() => CheckAsync(location, previous, _stop.Token));
            }

            ChooseActive();
            PersistState();
            var active = _active;
            var source = active?.Store;
            foreach (var location in _locations)
            {
                if (location.Store is not { } store)
                {
                    continue;
                }
                if (location.Maintenance is null or { IsCompleted: true })
                {
                    location.Maintenance = Task.Run(() => MaintainAsync(location, store, _stop.Token));
                }
                if (source is not null && location != active && location.Copy is null or { IsCompleted: true })
                {
                    location.CopyCancellation?.Dispose();
                    location.CopyCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    var token = location.CopyCancellation.Token;
                    location.Copy = Task.Run(() => CopyAsync(location, store, source, token));
                }
            }
        }
    }

    public void Activate(string directory)
    {
        lock (_gate)
        {
            var destination = _locations.FirstOrDefault(location =>
                location.Settings.Directory.Equals(directory, RecordingLocation.PathComparison));
            if (destination is null || destination.Store is null || !destination.Synchronized)
            {
                throw new InvalidOperationException("Choose a healthy recording folder that has finished catching up.");
            }
            if (destination != _active)
            {
                SetActive(destination, failed: false);
            }
        }
    }

    public Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization, double preSeconds,
        CancellationToken token, DateTimeOffset? triggeredAt = null) =>
        BeginTriggeredAsync(channel, name, initialization, preSeconds, new(), token, triggeredAt);

    public async Task<IEventWriter> BeginTriggeredAsync(int channel, string name, byte[] initialization, double preSeconds,
        RecordingTrigger trigger, CancellationToken token, DateTimeOffset? triggeredAt = null)
    {
        Location location;
        FileRecordingStore store;
        lock (_gate)
        {
            ChooseActive();
            location = _active ?? throw new IOException("All recording folders are unavailable.");
            store = location.Store!;
        }

        var writer = await RunIoAsync(location, store,
            ct => store.BeginTriggeredAsync(channel, name, initialization, preSeconds, trigger, ct, triggeredAt),
            token, FileRecordingStore.AbandonAsync);
        return new RoutedWriter(this, location, store, writer);
    }

    public void Dispose()
    {
        _ = _stop.CancelAsync();
        // A blocked filesystem call can outlive cancellation. Leave its token available until it returns.
    }

    public bool CanReopen(string directory)
    {
        lock (_gate)
        {
            return _canOpen?.Invoke(directory) != false && !_locations.Any(location =>
                location.Settings.Directory.Equals(directory, RecordingLocation.PathComparison) &&
                (location.Operations > 0 || location.Health is { IsCompleted: false } ||
                 location.Maintenance is { IsCompleted: false } || location.Copy is { IsCompleted: false }));
        }
    }

    public async Task StopAsync()
    {
        Dispose();
        Task[] jobs;
        lock (_gate)
        {
            jobs = _locations.SelectMany(location => new[] { location.Health, location.Maintenance, location.Copy })
                .Append(_persistence).OfType<Task>().ToArray();
        }
        try
        {
            await Task.WhenAll(jobs).WaitAsync(_ioTimeout);
        }
        catch (TimeoutException)
        {
            // Reopening a folder remains blocked until its filesystem calls actually return.
        }
    }

    private FileRecordingStore Open(Location location) => new(_settings with
    {
        Directory = location.Settings.Directory,
        MaximumStorageGigabytes = location.Settings.MaximumStorageGigabytes
    }, _ffmpeg, _publisher, location: location.Settings);

    private async Task CheckAsync(Location location, FileRecordingStore? previous, CancellationToken token)
    {
        FileRecordingStore? store = previous;
        try
        {
            location.Settings.EnsureAvailable();
            store ??= Open(location);
            if (previous is null)
            {
                await store.CheckCapacityAsync(location.RequiredBytes, token);
            }
            var through = store.CompletedAfter(null, 1).FirstOrDefault()?.EndedAt;
            var freeBytes = RecordingStorage.FreeBytes(location.Settings.ArchiveDirectory);
            lock (_gate)
            {
                if (location.Store != previous || token.IsCancellationRequested ||
                    DateTimeOffset.UtcNow - location.HealthStarted >= _ioTimeout)
                {
                    return;
                }
                location.Store = store;
                location.Library = store;
                location.Through = through;
                location.FreeBytes = freeBytes;
                location.Status = "ready";
                ChooseActive();
            }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            lock (_gate)
            {
                if (location.Store == previous)
                {
                    location.Library = store;
                    Fail(location, Classify(ex), ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task MaintainAsync(Location location, FileRecordingStore store, CancellationToken token)
    {
        try
        {
            await store.MaintainAsync(token);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            lock (_gate)
            {
                if (location.Store == store)
                {
                    Fail(location, Classify(ex), ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task CopyAsync(Location location, FileRecordingStore store, FileRecordingStore source, CancellationToken token)
    {
        try
        {
            var sameSource = location.CopySource == source;
            var cursor = sameSource ? location.Cursor : null;
            // Check recent footage first so a large catch-up cannot starve new recordings.
            var recent = source.CompletedAfter(null, BatchSize);
            await CopyBatch(recent);
            var sweep = !sameSource || cursor is not null || DateTimeOffset.UtcNow >= location.NextSweep;
            var older = Array.Empty<RecordingEvent>();
            if (recent.Length == BatchSize && sweep)
            {
                older = source.CompletedAfter(cursor ?? recent[^1], BatchSize);
                await CopyBatch(older);
            }
            lock (_gate)
            {
                if (location.Store == store && _active?.Store == source)
                {
                    location.CopySource = source;
                    location.Cursor = older.Length == BatchSize ? older[^1] : null;
                    location.Synchronized = location.Cursor is null;
                    if (sweep && location.Synchronized)
                    {
                        location.NextSweep = DateTimeOffset.UtcNow.AddMinutes(1);
                    }
                    location.CopyStatus = null;
                }
            }

            async Task CopyBatch(RecordingEvent[] batch)
            {
                foreach (var item in batch)
                {
                    token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_active?.Store != source || location.Store != store)
                        {
                            return;
                        }
                    }
                    await store.MirrorAsync(source, item, token);
                }
            }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            lock (_gate)
            {
                if (location.Store == store && _active?.Store == source)
                {
                    location.Synchronized = false;
                    location.CopyStatus = ex is RecordingLocationException { Status: "full" } ? "full" : "catching-up";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void ChooseActive()
    {
        if (_active is not null || _stop.IsCancellationRequested)
        {
            return;
        }
        var preferred = _locations.FirstOrDefault(location =>
            location.Settings.Directory.Equals(_state.ActiveDirectory, RecordingLocation.PathComparison)) ?? _locations[0];
        if (preferred.Store is not null)
        {
            SetActive(preferred, failed: false);
        }
        else if (preferred.Status != "checking" && _locations.FirstOrDefault(location => location.Store is not null) is { } fallback)
        {
            SetActive(fallback, failed: true);
        }
    }

    private void SetActive(Location destination, bool failed)
    {
        var previous = _state.ActiveDirectory ?? _locations[0].Settings.Directory;
        _active = destination;
        foreach (var location in _locations)
        {
            location.Synchronized = false;
            if (location.CopyCancellation is { } cancellation)
            {
                _ = cancellation.CancelAsync();
            }
        }
        var state = failed
            ? new RecordingStorageState(destination.Settings.Directory, DateTimeOffset.UtcNow, previous, destination.Through)
            : _state with { ActiveDirectory = destination.Settings.Directory };
        _state = state;
        PersistState();
    }

    private void PersistState()
    {
        if (_state == _persistedState || _persistence is { IsCompleted: false })
        {
            return;
        }
        _persistence = Task.Run(() =>
        {
            while (true)
            {
                RecordingStorageState state;
                lock (_gate)
                {
                    state = _state;
                    if (state == _persistedState)
                    {
                        return;
                    }
                }
                try
                {
                    _persist(state);
                    lock (_gate)
                    {
                        _persistedState = state;
                        _notice = "";
                    }
                }
                catch (Exception ex) when (IsStorageError(ex))
                {
                    lock (_gate)
                    {
                        _notice = "The active recording folder could not be saved for the next server restart.";
                    }
                    return;
                }
            }
        });
    }

    private void Fail(Location location, string status, Exception? error = null)
    {
        location.Store = null;
        location.Status = status;
        location.CopyStatus = null;
        location.Synchronized = false;
        if (location.CopyCancellation is { } cancellation)
        {
            _ = cancellation.CancelAsync();
        }
        if (error is RecordingLocationException { RequiredBytes: > 0 } capacityError)
        {
            location.RequiredBytes = capacityError.RequiredBytes;
        }
        if (_active == location)
        {
            _active = null;
            ChooseActive();
        }
    }

    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException;

    private static string Classify(Exception exception)
    {
        if (exception is RecordingLocationException error)
        {
            return error.Status;
        }
        return exception is DirectoryNotFoundException or DriveNotFoundException ? "offline" : "error";
    }

    private async Task<T> RunIoAsync<T>(Location location, FileRecordingStore store,
        Func<CancellationToken, Task<T>> action, CancellationToken token, Func<T, ValueTask>? abandon = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        lock (_gate)
        {
            if (location.Store != store)
            {
                throw new IOException("The recording folder is unavailable.");
            }
            location.Operations++;
        }
        var operationToken = deadline.Token;
        var work = Task.Run(async () =>
        {
            try
            {
                operationToken.ThrowIfCancellationRequested();
                var result = await action(operationToken);
                if (operationToken.IsCancellationRequested && abandon is not null)
                {
                    await abandon(result);
                }
                operationToken.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                lock (_gate)
                {
                    location.Operations--;
                }
            }
        });
        try
        {
            return await work.WaitAsync(_ioTimeout, token);
        }
        catch (TimeoutException)
        {
            _ = deadline.CancelAsync();
            Fault("stalled");
            _ = ObserveAsync(work);
            throw new IOException("The recording folder stopped responding.");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Fault(Classify(ex), ex);
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = ObserveAsync(work);
            throw;
        }

        void Fault(string status, Exception? error = null)
        {
            lock (_gate)
            {
                if (location.Store == store)
                {
                    Fail(location, status, error);
                }
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (IsStorageError(ex) || ex is OperationCanceledException)
        {
        }
    }

    private sealed class Location(RecordingLocation settings)
    {
        public RecordingLocation Settings { get; } = settings;
        public FileRecordingStore? Store { get; set; }
        public FileRecordingStore? Library { get; set; }
        public string Status { get; set; } = "checking";
        public string? CopyStatus { get; set; }
        public DateTimeOffset? Through { get; set; }
        public long? FreeBytes { get; set; }
        public long RequiredBytes { get; set; }
        public DateTimeOffset HealthStarted { get; set; }
        public Task? Health { get; set; }
        public Task? Maintenance { get; set; }
        public Task? Copy { get; set; }
        public CancellationTokenSource? CopyCancellation { get; set; }
        public DateTimeOffset NextSweep { get; set; }
        public RecordingEvent? Cursor { get; set; }
        public FileRecordingStore? CopySource { get; set; }
        public bool Synchronized { get; set; }
        public int Operations { get; set; }
    }

    private sealed class RoutedWriter(RecordingStoragePool owner, Location location, FileRecordingStore store, IEventWriter writer) : IEventWriter
    {
        private readonly SemaphoreSlim _operations = new(1, 1);
        private int _completed;
        public string? Id => writer.Id;

        public Task AppendAsync(VideoFragment fragment, CancellationToken token) =>
            RunAsync(ct => writer.AppendAsync(fragment, ct), token);

        public Task AddTriggerAsync(RecordingTrigger trigger, CancellationToken token) =>
            RunAsync(ct => writer.AddTriggerAsync(trigger, ct), token);

        private Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token, bool completing = false)
        {
            lock (owner._gate)
            {
                if (!completing && owner._active != location)
                {
                    throw new IOException("Recording storage changed. Opening a new clip on the active drive.");
                }
            }
            return owner.RunIoAsync(location, store, async ct =>
            {
                await _operations.WaitAsync(ct);
                try
                {
                    await action(ct);
                    return true;
                }
                finally
                {
                    _operations.Release();
                }
            }, token);
        }

        public async Task CompleteAsync(bool interrupted, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }
            try
            {
                await RunAsync(ct => writer.CompleteAsync(interrupted, ct), token, completing: true);
            }
            catch (Exception ex) when (IsStorageError(ex) || ex is OperationCanceledException)
            {
                lock (owner._gate)
                {
                    location.Operations++;
                }
                _ = ObserveAsync(Task.Run(async () =>
                {
                    await _operations.WaitAsync();
                    try
                    {
                        await FileRecordingStore.AbandonAsync(writer);
                    }
                    finally
                    {
                        _operations.Release();
                        lock (owner._gate)
                        {
                            location.Operations--;
                        }
                    }
                }));
            }
        }

        public ValueTask DisposeAsync() => new(CompleteAsync(true, CancellationToken.None));
    }
}
