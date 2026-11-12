using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using System.Text.Json;

namespace SecurityCameraViewer.Web;

public interface IRecordingConnection
{
    Task ChangeConnectionAsync(ViewerSettings connection, string password);
}

/// <summary>Web host lifecycle/configuration only. RecordingCamera and its interfaces own capture behaviour.</summary>
public sealed class RecordingService : BackgroundService, IRecordingConnection, IMotionActionCatalog
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly RecordingSettingsStore _settingsStore;
    private readonly FfmpegProcess _ffmpeg;
    private readonly ICameraEventPublisher _publisher;
    private string _armingPath;
    private readonly string _integrationDirectory;
    private Dictionary<int, ArmingOverride> _arming;
    private bool _storageWarning;
    public RecordingSettings Settings => _settings;
    public MotionAction? FindAction(string id)
    {
        return Settings.Actions.FirstOrDefault(action => action.Id == id);
    }

    public RecordingCamera[] Cameras => Volatile.Read(ref _cameras);
    public SecurityCameraViewer.Core.ViewerSettings Connection => _connection;
    private readonly IMotionDetectorFactory _detectors;
    private readonly IAiModelRegistry _models;
    private readonly IMotionFrameSource _frames;
    private readonly IEncodedVideoSource _video;
    private readonly BatteryCameraRegistry _batteryCameras;
    private readonly DetectionHistoryOptions _detectionHistoryOptions;
    private ViewerSettings _connection;
    private string _password;
    private RecordingSettings _settings;
    private RecordingStoragePool _storage;
    private FileRecordingStore _emptyStore;
    private readonly TimeSpan _storageIoTimeout;
    private RecordingCamera[] _cameras = [];
    private string _notice = "";
    private readonly bool _demo;
    private bool _stopping;
    private RecordingStoragePool Storage => Volatile.Read(ref _storage);
    public FileRecordingStore Store => Storage.ReadableStore ?? _emptyStore;

    public RecordingService(RecordingSettingsStore settingsStore, WebSettingsStore connectionStore, IConfiguration configuration,
        FfmpegProcess ffmpeg, IMotionDetectorFactory detectors, IMotionFrameSource frames, IEncodedVideoSource video, ICameraEventPublisher publisher, IntegrationSettings integrations,
        IAiModelRegistry models, BatteryCameraRegistry? batteryCameras = null)
    {
        _models = models;
        var timeout = configuration.GetValue("RecordingStorage:IoTimeoutSeconds", 15d);
        if (!double.IsFinite(timeout) || timeout <= 0 || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1).TotalSeconds)
        {
            throw new ArgumentException("RecordingStorage:IoTimeoutSeconds must be a positive timer duration.");
        }
        _storageIoTimeout = TimeSpan.FromSeconds(timeout);
        _detectionHistoryOptions = configuration.GetSection("DetectionOverlay").Get<DetectionHistoryOptions>() ?? new();
        _detectionHistoryOptions.Validate();
        _batteryCameras = batteryCameras ?? new();
        _publisher = publisher;
        _integrationDirectory = integrations.DirectoryPath;
        _armingPath = Path.Combine(integrations.DirectoryPath, "arming.json");
        _arming = File.Exists(_armingPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, ArmingOverride>>(File.ReadAllText(_armingPath)) ?? [] : [];
        _settingsStore = settingsStore;
        _ffmpeg = ffmpeg;
        _detectors = detectors;
        _frames = frames;
        _video = video;
        _demo = configuration.GetValue<bool>("demo");
        (_connection, _password, _) = connectionStore.Load(_demo);
        settingsStore.SelectConfiguration(_connection.ConfigurationId);
        _settings = DetectionTemplateResolver.Migrate(BindSources(settingsStore.Load().WithZoneIds(), _connection));
        LoadArming();
        // Demo must never arm real saved cameras or write into the real recording library.
        if (_demo)
        {
            _settings = _settings with
            {
                Enabled = false,
                Directory = Path.Combine(Path.GetTempPath(), "SecurityCameraViewer-demo-" + Environment.ProcessId),
                PrimaryLocationId = null,
                Mirrors = []
            };
        }

        _emptyStore = CreateEmptyStore();
        _storage = CreateStorage(_settings);
    }

    public object Snapshot()
    {
        var storage = Storage;
        var store = storage.ReadableStore ?? _emptyStore;
        var locations = storage.Snapshot();
        return new
        {
            settings = _settings,
            detector = _detectors.Name,
            cameras = Volatile.Read(ref _cameras).Select(c => c.State).ToArray(),
            usedBytes = store.UsedBytes,
            eventCount = store.Count,
            recordedCameras = store.Cameras,
            storageLocations = locations,
            storageState = storage.State,
            notice = string.Join(" ", new[] { _settingsStore.Notice, _notice, store.Notice, storage.Notice,
                _settings.Enabled && !locations.Any(location => location.Active) ? "No recording folder is currently available." : ""
            }.Where(s => s.Length > 0))
        };
    }

    public async Task<object> DetectionSnapshotAsync(CancellationToken token, DateTimeOffset? since = null)
    {
        await _transition.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            return new
            {
                configurationId = _connection.ConfigurationId,
                generatedAt = now,
                historySeconds = _detectionHistoryOptions.Seconds,
                maximumHistorySamples = _detectionHistoryOptions.MaximumSamplesPerCamera,
                enabled = _settings.Enabled,
                methods = DetectionMethods.All.Select(method => new
                {
                    method.Id,
                    method.Name,
                    shortName = method.Id switch
                    {
                        DetectionMethods.Adaptive => "Motion",
                        DetectionMethods.Ai => "AI",
                        _ => method.Name
                    }
                }).ToArray(),
                models = _models.Models.Select(model => new { model.Id, model.Name }).ToArray(),
                cameras = Volatile.Read(ref _cameras).Select(camera => new
                {
                    camera.State.Channel,
                    camera.State.Status,
                    history = camera.DetectionHistory.Snapshot(now, since),
                    detections = camera.State.Detections.Where(item => item.ExpiresAt > now).Take(128).Select(item => new
                    {
                        observation = item,
                        ageSeconds = Math.Max(0, (now - item.CapturedAt).TotalSeconds),
                        remainingSeconds = Math.Max(0, (item.ExpiresAt - now).TotalSeconds)
                    }).ToArray()
                }).ToArray()
            };
        }
        finally
        {
            _transition.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _transition.WaitAsync(stoppingToken);
            try
            {
                StartCameras();
            }
            finally
            {
                _transition.Release();
            }
            while (!stoppingToken.IsCancellationRequested)
            {
                var storage = Storage;
                storage.Poll();
                CheckStorage(storage);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public async Task<RecordingEvent[]> SaveRewindAsync(RewindRecordingSelection selection,
        RewindRecordingExporter exporter, CancellationToken token)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (_stopping || _connection.ConfigurationId != selection.ConfigurationId)
            {
                throw new InvalidOperationException("The active configuration changed. Select the recording again.");
            }
            return await exporter.SaveAsync(selection, Storage.ActiveStore ??
                throw new IOException("No recording folder is currently available."), token);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ConfigureAsync(RecordingSettings settings, CancellationToken token)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (_stopping)
            {
                return;
            }

            if (settings.ConfigurationId != _connection.ConfigurationId)
            {
                throw new InvalidOperationException("The active configuration changed. Reopen recording settings before saving.");
            }
            if (settings.Validate(_models) is { } settingsError)
            {
                throw new ArgumentException(settingsError);
            }

            if (settings.Cameras.Any(c => c.EffectiveMode == RecordingModes.AvailableVideo &&
                !_connection.Sources.Any(source => source.Channel == c.Channel && source.MotionActivated)))
            {
                throw new ArgumentException("Recording available video requires Motion-activated / battery camera mode in Camera setup.");
            }

            settings = DetectionTemplateResolver.Migrate(BindSources(settings, _connection));
            if (settings.Validate(_models) is { } migratedError)
            {
                throw new ArgumentException(migratedError);
            }

            if (_demo && (!string.Equals(settings.Directory, _settings.Directory, StringComparison.Ordinal) ||
                settings.Mirrors.Length > 0 || settings.RegisterPrimaryReplacement))
            {
                throw new InvalidOperationException("Demo recordings must stay in the demo folder.");
            }
            await StopCamerasAsync();
            var previous = Storage;
            await previous.StopAsync();
            try
            {
                settings = PrepareStorageSettings(settings.WithZoneIds(migrate: false), previous);
                var next = CreateStorage(settings, previous);
                if (!_demo)
                {
                    try
                    {
                        await _settingsStore.SaveAsync(settings, token);
                    }
                    catch
                    {
                        next.Dispose();
                        throw;
                    }
                }

                _settings = settings;
                Volatile.Write(ref _storage, next);
                next.Poll();
                _notice = "";
            }
            catch
            {
                var restored = CreateStorage(_settings, previous);
                Volatile.Write(ref _storage, restored);
                restored.Poll();
                throw;
            }
            finally
            {
                StartCameras();
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ChangeConnectionAsync(ViewerSettings connection, string password)
    {
        await _transition.WaitAsync();
        try
        {
            if (_stopping)
            {
                return;
            }

            await StopCamerasAsync();
            var previous = Storage;
            await previous.StopAsync();
            var sameConfiguration = _connection.ConfigurationId == connection.ConfigurationId;
            if (!sameConfiguration)
            {
                _settingsStore.SelectConfiguration(connection.ConfigurationId);
                _settings = _settingsStore.Load().WithZoneIds();
            }

            var oldIds = _connection.Channels.ToDictionary(channel => channel, _connection.CameraId);
            _connection = connection;
            _password = password;
            _settings = DetectionTemplateResolver.Migrate(BindSources(_settings, connection));
            if (!_demo)
            {
                await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            }

            LoadArming();
            foreach (var channel in connection.Channels.Where(channel => sameConfiguration && oldIds.GetValueOrDefault(channel) != connection.CameraId(channel)))
            {
                _arming.Remove(channel);
            }

            if (!_demo)
            {
                IntegrationSettings.WritePrivate(_armingPath, System.Text.Json.JsonSerializer.Serialize(_arming));
            }
            _emptyStore = CreateEmptyStore();
            var next = CreateStorage(_settings, previous);
            Volatile.Write(ref _storage, next);
            next.Poll();
            StartCameras();
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task SetArmingAsync(int channel, ArmingOverride value, CancellationToken token)
    {
        if (!_connection.Channels.Contains(channel))
        {
            throw new ArgumentException("Unknown camera.");
        }

        if (value.Mode is not ("schedule" or "armed" or "disarmed") || value.Mode != "schedule" &&
            (value.ExpiresAt is null || value.ExpiresAt <= DateTimeOffset.UtcNow || value.ExpiresAt > DateTimeOffset.UtcNow.AddDays(7)))
        {
            throw new ArgumentException("Use schedule, or armed/disarmed with a future expiry within seven days.");
        }

        await _transition.WaitAsync(token);
        try
        {
            var next = new Dictionary<int, ArmingOverride>(_arming) { [channel] = value };
            IntegrationSettings.WritePrivate(_armingPath, System.Text.Json.JsonSerializer.Serialize(next));
            _arming = next;
            Cameras.FirstOrDefault(c => c.State.Channel == channel)?.SetArming(value);
        }
        finally
        {
            _transition.Release();
        }
    }

    public ArmingOverride Arming(int channel) => _arming.GetValueOrDefault(channel) ?? new("schedule");
    public long? FreeBytes() => Storage.Snapshot().FirstOrDefault(location => location.Active)?.FreeBytes;

    private void CheckStorage(RecordingStoragePool storage)
    {
        var locations = storage.Snapshot();
        var freeBytes = locations.FirstOrDefault(location => location.Active)?.FreeBytes;
        var warning = _settings.Enabled && (storage.ActiveStore is null || freeBytes is < 128L * 1024 * 1024 ||
            locations.Any(location => location.Status is "offline" or "wrong-folder" or "stalled" or "error" or "full"));
        if (warning == _storageWarning)
        {
            return;
        }

        _storageWarning = warning;
        _publisher.Publish(new(warning ? "storage.warning" : "storage.recovered", Data: new
        {
            freeBytes,
            usedBytes = storage.ReadableStore?.UsedBytes ?? 0
        }));
    }

    private void StartCameras()
    {
        if (!_settings.Enabled || _stopping)
        {
            return;
        }

        var cameras = _connection.Channels
            .Where(channel => _settings.Camera(channel).Enabled)
            .Select(channel =>
            {
                var camera = new RecordingCamera(
                    _connection, _password, channel, _settings, _detectors, _frames, _video, Storage, _publisher,
                    _batteryCameras.Get(_connection, _password, channel), _detectionHistoryOptions);
                camera.SetArming(_arming.GetValueOrDefault(channel) ?? new ArmingOverride("schedule"));
                return camera;
            })
            .ToArray();
        Volatile.Write(ref _cameras, cameras);
    }

    public int ChannelForId(string id) => _connection.Channels.FirstOrDefault(channel => _connection.CameraId(channel) == id);

    public async Task ActivateStorageAsync(string directory, string configurationId, CancellationToken token)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (_stopping || configurationId != _settings.ConfigurationId)
            {
                throw new InvalidOperationException("The active configuration changed. Reopen recording settings.");
            }
            Storage.Activate(RecordingLocation.Normalize(directory));
        }
        finally
        {
            _transition.Release();
        }
    }

    private FileRecordingStore CreateEmptyStore() => new(_settings with
    {
        Directory = Path.Combine(_integrationDirectory, ".unavailable-" + Guid.NewGuid().ToString("N"))
    }, _ffmpeg);

    private RecordingStoragePool CreateStorage(RecordingSettings settings, RecordingStoragePool? previous = null)
    {
        var path = Path.Combine(_integrationDirectory, $"recording-storage-{settings.ConfigurationId}.json");
        var state = new RecordingStorageState();
        try
        {
            if (!_demo && File.Exists(path))
            {
                state = JsonSerializer.Deserialize<RecordingStorageState>(File.ReadAllText(path)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _notice = "The last active recording folder could not be read.";
        }
        return new(settings, _ffmpeg, _publisher, _storageIoTimeout, state, next =>
        {
            if (!_demo)
            {
                IntegrationSettings.WritePrivate(path, JsonSerializer.Serialize(next));
            }
        }, previous is null ? null : previous.CanReopen);
    }

    private RecordingSettings PrepareStorageSettings(RecordingSettings settings, RecordingStoragePool previous)
    {
        var directory = RecordingLocation.Normalize(settings.Directory);
        var samePrimary = SamePath(directory, _settings.Directory);
        var primaryId = samePrimary ? _settings.PrimaryLocationId : null;
        var primary = new RecordingLocation(directory, settings.MaximumStorageGigabytes);
        if (settings.RegisterPrimaryReplacement || primaryId is null && (settings.Mirrors.Length > 0 || primary.HasIdentity()))
        {
            CheckIdle(directory);
            primaryId = primary.Register(settings.ConfigurationId).Id;
        }
        else if (primaryId is null && settings.Enabled && (!samePrimary || !_settings.Enabled))
        {
            Directory.CreateDirectory(directory);
            using var probe = new FileStream(Path.Combine(directory, ".write-check-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }

        var mirrors = settings.Mirrors.Select(mirror =>
        {
            var path = RecordingLocation.Normalize(mirror.Directory);
            var saved = _settings.Mirrors.FirstOrDefault(location => SamePath(path, location.Directory));
            var location = mirror with { Directory = path, Id = saved?.Id, RegisterReplacement = false };
            if (location.Id is not null && !mirror.RegisterReplacement)
            {
                return location;
            }
            CheckIdle(path);
            return location.Register(settings.ConfigurationId);
        }).ToArray();
        var ids = mirrors.Select(mirror => mirror.Id).Prepend(primaryId).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new ArgumentException("Choose different recording folders. Two paths identify the same registered folder.");
        }
        return settings with { Directory = directory, PrimaryLocationId = primaryId, RegisterPrimaryReplacement = false, Mirrors = mirrors };

        static bool SamePath(string path, string other) =>
            path.Equals(RecordingLocation.Normalize(other), RecordingLocation.PathComparison);

        void CheckIdle(string path)
        {
            if (!previous.CanReopen(path))
            {
                throw new InvalidOperationException("The previous recording folder is still busy. Try again after it responds.");
            }
        }
    }

    private void LoadArming()
    {
        _armingPath = Path.Combine(_integrationDirectory, _connection.ConfigurationId == "default" ? "arming.json" : $"arming-{_connection.ConfigurationId}.json");
        _arming = File.Exists(_armingPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, ArmingOverride>>(File.ReadAllText(_armingPath)) ?? [] : [];
    }

    private static RecordingSettings BindSources(RecordingSettings settings, ViewerSettings connection)
    {
        return settings with
        {
            ConfigurationId = connection.ConfigurationId,
            // Disabled cameras retain their rules; capture uses only enabled channels.
            Cameras = connection.WithSources().Sources.Select(source =>
            {
                var channel = source.Channel;
                var id = connection.CameraId(channel);
                var previous = settings.Cameras.FirstOrDefault(camera => camera.CameraId == id ||
                    camera.CameraId is null && camera.Channel == channel && id == $"camera-{channel}");
                return (previous ?? new CameraRecordingSettings()) with
                {
                    Channel = channel,
                    CameraId = id,
                    Mode = previous?.EffectiveMode == RecordingModes.AvailableVideo && !source.MotionActivated
                        ? RecordingModes.Event : previous?.EffectiveMode ?? RecordingModes.Event,
                    RecordWhenStreaming = previous?.EffectiveMode == RecordingModes.AvailableVideo && source.MotionActivated
                };
            }).ToArray()
        };
    }

    private async Task StopCamerasAsync()
    {
        var cameras = Interlocked.Exchange(ref _cameras, []);
        await Task.WhenAll(cameras.Select(async camera => await camera.DisposeAsync()));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel maintenance before waiting for its lock. A interrupted remux is resumed at startup.
        _stopping = true;
        await base.StopAsync(cancellationToken);
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            await StopCamerasAsync();
            await Storage.StopAsync();
        }
        finally
        {
            _transition.Release();
        }
    }
}
