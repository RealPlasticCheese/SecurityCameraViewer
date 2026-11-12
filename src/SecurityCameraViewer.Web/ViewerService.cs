using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web.Streaming;

namespace SecurityCameraViewer.Web;

public sealed record ConnectionRequest(ViewerSettings Settings, string? Password, Dictionary<string, CameraCredentials>? CameraCredentials = null)
{
    public override string ToString() => "Camera connection request";
}

public sealed class ViewerService : BackgroundService, IStreamingCameraProvider
{
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly WebSettingsStore _store;
    private readonly CameraEnhancementStore _enhancements;
    private readonly IRecordingConnection? _recording;
    private readonly string _ffmpeg;
    private readonly BatteryCameraRegistry _batteryCameras;
    private readonly LiveMediaHub _liveMedia;
    private readonly LiveMediaHub _compatibleLiveMedia;
    private readonly LiveMediaHub _cameraVideo;
    private readonly LiveMediaOptions _mediaOptions;
    private readonly TalkbackHub _talkback;
    private readonly ICameraConnectionRouter? _connectionRouter;
    private readonly bool _previewOnDemand;
    private ViewerSettings _settings;
    private string _password;
    private string _notice;
    private ConfigurationCatalog _catalog;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, CameraCredentials>> _credentials = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _passwords = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CameraDiagnostic> _diagnostics = new();
    private WebCamera[] _cameras = [];
    private volatile bool _connecting;
    private int _generation;
    private StreamingConnection _streamingConnection;
    private LiveRewindSettings _rewindSettings;
    public StreamingConnection StreamingConnection => Volatile.Read(ref _streamingConnection);
    public bool Demo
    {
        get;
    }
    public WebCamera[] Cameras => Volatile.Read(ref _cameras);

    public ViewerService(WebSettingsStore store, IConfiguration configuration, IRecordingConnection? recording = null,
        BatteryCameraRegistry? batteryCameras = null, LiveMediaHub? liveMedia = null, TalkbackHub? talkback = null,
        ICameraConnectionRouter? connectionRouter = null, CameraEnhancementStore? enhancements = null)
    {
        _batteryCameras = batteryCameras ?? new();
        _store = store;
        _enhancements = enhancements ?? new(store.SettingsStore);
        _rewindSettings = store.SettingsStore.Read().LiveRewind;
        _recording = recording;
        _connectionRouter = connectionRouter;
        _previewOnDemand = configuration.GetValue("preview-on-demand", true);
        Demo = configuration.GetValue<bool>("demo");
        _ffmpeg = configuration["ffmpeg"] ?? "ffmpeg";
        var mediaOptions = configuration.GetSection("LiveMedia").Get<LiveMediaOptions>() ?? new();
        mediaOptions.Validate();
        _mediaOptions = mediaOptions;
        _liveMedia = liveMedia ?? new(new FfmpegLiveMediaSource(new FfmpegProcess(_ffmpeg), connectionRouter, mediaOptions), mediaOptions);
        _compatibleLiveMedia = new(new FfmpegLiveMediaSource(new FfmpegProcess(_ffmpeg), connectionRouter,
            mediaOptions with { ForceTranscoding = true }), mediaOptions);
        _cameraVideo = new(new CameraVideoSource(new FfmpegProcess(_ffmpeg), connectionRouter), mediaOptions);
        _talkback = talkback ?? new(new RtspAudioBackchannel());
        (_settings, _password, _notice) = store.Load(Demo);
        _catalog = Demo ? new()
        {
            Configurations = [_settings]
        } : store.LoadCatalog();
        foreach (var saved in _catalog.Configurations)
        {
            _credentials[saved.ConfigurationId] = saved.Credentials;
            _passwords[saved.ConfigurationId] = Demo ? _password : store.LoadPassword(saved.ConfigurationId);
        }
        _streamingConnection = new(_settings, _password);
    }

    public object Snapshot() => new
    {
        settings = _settings,
        enhancements = _enhancements.List(_settings.ConfigurationId),
        hasPassword = _password.Length > 0,
        demo = Demo,
        connecting = _connecting,
        generation = _generation,
        connected = Cameras.Length > 0,
        notice = _notice,
        cameras = Cameras.Select(c => c.State with
        {
            Live = c.State.Live || _cameraVideo.IsLive(c.State.Channel)
        }).ToArray(),
        playback = new
        {
            maximumQueuedBytes = _mediaOptions.MaximumQueuedBytes,
            maximumQueuedPackets = _mediaOptions.MaximumQueuedPackets,
            retryDelaysSeconds = _settings.RetryDelaysSeconds,
            acknowledgementTimeoutSeconds = CameraVideoEndpoints.AcknowledgementTimeout.TotalSeconds
        }
    };

    public object Configurations() => new
    {
        activeId = _settings.ConfigurationId,
        defaultRetryDelaysSeconds = CameraRetrySchedule.DefaultDelaysSeconds,
        configurations = _catalog.Configurations.Select(c => c.WithSources()),
        credentials = _settings.Sources.Select(c => new { c.Id, hasPassword = _settings.CameraPassword(c.Channel, _password).Length > 0 })
    };

    public object ConfigurationPasswords(string id)
    {
        var settings = _catalog.Configurations.FirstOrDefault(c => c.ConfigurationId == id)
            ?? throw new ArgumentException("Unknown configuration.");
        var credentials = _credentials.GetValueOrDefault(id) ?? _store.LoadCredentials(id);
        return new
        {
            sharedPassword = _passwords.GetValueOrDefault(id) ?? _password,
            cameraPasswords = settings.WithSources().Sources.ToDictionary(c => c.Id, c => credentials.GetValueOrDefault(c.Id)?.Password ?? "")
        };
    }

    public string Metadata(bool includePasswords = false)
    {
        var settings = _settings.WithSources();
        var report = new
        {
            schemaVersion = 1,
            exportedAt = DateTimeOffset.UtcNow,
            applicationVersion = typeof(ViewerService).Assembly.GetName().Version?.ToString(),
            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            discoveryRunsOn = "Viewer server; IPv4 multicast on selected local interfaces",
            networkInterfaces = OnvifClient.NetworkAddresses(),
            redaction = includePasswords
                ? "Passwords are included because password viewing was enabled. Review before sharing. Not a settings backup."
                : "Credentials and URL query values omitted. Camera names, IP addresses and device identifiers remain; review before sharing. Not a settings backup.",
            settings,
            passwords = includePasswords
                ? settings.Sources.ToDictionary(c => c.Id, c => _settings.CameraPassword(c.Channel, _password))
                : null,
            live = Cameras.Select(c => c.State).ToArray(),
            tests = _diagnostics.Values.ToArray()
        };

        return CameraDiagnostics.Export(report, includePasswords);
    }

    public async Task<CameraDiagnostic> DiagnoseAsync(int channel, bool substream, CancellationToken token)
    {
        var settings = _settings;
        if (!settings.Channels.Contains(channel))
        {
            throw new ArgumentException("Unknown camera.");
        }

        var result = await CameraDiagnostics.TestAsync(settings with
        {
            Substream = substream
        }, _password, channel, _ffmpeg, token);
        if (ReferenceEquals(settings, _settings))
        {
            _diagnostics[$"{channel}/{substream}"] = result;
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_settings.AutoConnect)
            {
                var recovery = new CameraRecovery(_settings.RetryDelaysSeconds);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!stoppingToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        recovery.StartAttempt(clock.Elapsed);
                        await ConnectAsync();
                        break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _notice = $"Camera connection interrupted ({ex.GetType().Name}). Retrying automatically…";
                        recovery.Failed(clock.Elapsed);
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.Token);
                        await recovery.WaitForRetryAsync(() => clock.Elapsed, wait.Token);
                    }
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task ConnectAsync()
    {
        await _transition.WaitAsync(_lifetime.Token);
        try
        {
            await ConnectCoreAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task ConnectCoreAsync()
    {
        _connecting = true;
        try
        {
            await StopCamerasAsync();
            Interlocked.Increment(ref _generation);
            _notice = "Connecting to cameras independently…";
            if (_settings.Sources.Length == 0)
            {
                var probe = await RtspProbe.DescribeAsync(_settings, _password, cancellationToken: _lifetime.Token);
                _notice = (Demo ? "TEST VIDEO — local test pattern. " : "") + probe.Message;
                if (probe.StatusCode is 401 or 403)
                {
                    return;
                }
            }

            _lifetime.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref _cameras, [.. _settings.Channels.Select(channel => new WebCamera(_settings, _password, channel, _ffmpeg, _generation,
                _batteryCameras.Get(_settings, _password, channel), _connectionRouter, onDemand: _previewOnDemand))]);
            await ApplyRewindAsync();
        }
        finally
        {
            _connecting = false;
        }
    }

    public async Task DisconnectAsync()
    {
        await _transition.WaitAsync(_lifetime.Token);
        try
        {
            await StopCamerasAsync();
            _notice = "Disconnected. Click Connect to resume live viewing.";
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task RefreshMediaInputsAsync(CancellationToken token)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (_recording is not null)
            {
                await _recording.ChangeConnectionAsync(_settings, _password);
            }

            if (Cameras.Length > 0)
            {
                await ConnectCoreAsync();
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ConfigureAsync(ConnectionRequest request)
    {
        await _transition.WaitAsync(_lifetime.Token);
        try
        {
            if (request.Settings.Validate() is { } error)
            {
                throw new ArgumentException(error);
            }

            var settings = request.Settings.Normalize();
            var credentials = new Dictionary<string, CameraCredentials>(_credentials.GetValueOrDefault(settings.ConfigurationId) ?? _store.LoadCredentials(settings.ConfigurationId));
            foreach (var entry in request.CameraCredentials ?? [])
            {
                if (entry.Value is null || entry.Value.Password is null || entry.Value.Password.Length > 4096 || entry.Value.OnvifPassword?.Length > 4096 || entry.Value.OnvifUsername?.Length > 256)
                {
                    throw new ArgumentException("Camera credentials are invalid.");
                }

                var previous = credentials.GetValueOrDefault(entry.Key);
                credentials[entry.Key] = entry.Value with
                {
                    OnvifUsername = entry.Value.OnvifUsername ?? previous?.OnvifUsername,
                    OnvifPassword = entry.Value.OnvifPassword ?? previous?.OnvifPassword
                };
            }

            settings = settings with
            {
                Credentials = credentials
            };
            var catalog = _catalog with
            {
                ActiveId = settings.ConfigurationId,
                Configurations = [.. _catalog.Configurations.Where(c => c.ConfigurationId != settings.ConfigurationId), settings]
            };
            var password = request.Password ?? _passwords.GetValueOrDefault(settings.ConfigurationId) ?? _password;
            if (!Demo)
            {
                await _store.SaveConfigurationAsync(settings, password, _lifetime.Token);
            }

            await StopCamerasAsync();
            Interlocked.Increment(ref _generation);
            _password = password;
            _passwords[settings.ConfigurationId] = _password;
            _credentials[settings.ConfigurationId] = credentials;
            _catalog = catalog;
            _settings = settings;
            Volatile.Write(ref _streamingConnection, new(_settings, _password));
            _diagnostics.Clear();
            if (_recording is not null)
            {
                await _recording.ChangeConnectionAsync(_settings, _password);
            }

            await ConnectCoreAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    public Task ActivateAsync(string id)
    {
        var settings = _catalog.Configurations.FirstOrDefault(c => c.ConfigurationId == id)
            ?? throw new ArgumentException("Unknown configuration.");
        return ConfigureAsync(new(settings, null));
    }

    public async Task DeleteConfigurationAsync(string id)
    {
        await _transition.WaitAsync(_lifetime.Token);
        try
        {
            if (id == _settings.ConfigurationId)
            {
                throw new InvalidOperationException("Select another configuration before deleting the active one.");
            }

            var catalog = _catalog with
            {
                Configurations = _catalog.Configurations.Where(c => c.ConfigurationId != id).ToArray()
            };
            if (!Demo)
            {
                await _store.DeleteConfigurationAsync(id, _lifetime.Token);
            }

            _catalog = catalog;
            _credentials.TryRemove(id, out _);
            _passwords.TryRemove(id, out _);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ToggleQualityAsync()
    {
        await _transition.WaitAsync(_lifetime.Token);
        try
        {
            var settings = _settings with
            {
                Substream = !_settings.Substream
            };
            if (!Demo)
            {
                await _store.SaveConfigurationAsync(settings, _password, _lifetime.Token);
            }

            _settings = settings;
            _catalog = _catalog with
            {
                Configurations = _catalog.Configurations.Select(c => c.ConfigurationId == settings.ConfigurationId ? settings : c).ToArray()
            };
            Volatile.Write(ref _streamingConnection, new(_settings, _password));
            if (Cameras.Length > 0)
            {
                await ConnectCoreAsync();
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    public Task<ProbeResult> ProbeAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        var settings = request.Settings with
        {
            Sources = request.Settings.Sources.Select(c => c with { Enabled = true }).ToArray(),
            Credentials = request.CameraCredentials ?? _settings.Credentials
        };
        return RtspProbe.DescribeAsync(settings, request.Password ?? _password, settings.Channels.First(), cancellationToken);
    }

    public Task<CameraDiagnostic> PreviewAsync(ConnectionRequest request, CancellationToken token)
    {
        if (request.Settings?.Validate() is not null || request.Settings is null)
        {
            throw new ArgumentException(request.Settings?.Validate() ?? "Supply camera settings.");
        }

        var credentials = new Dictionary<string, CameraCredentials>(_credentials.GetValueOrDefault(request.Settings.ConfigurationId) ?? _store.LoadCredentials(request.Settings.ConfigurationId));
        foreach (var entry in request.CameraCredentials ?? [])
        {
            if (entry.Value?.Password is null || entry.Value.Password.Length > 4096)
            {
                throw new ArgumentException("Enter valid camera credentials.");
            }

            credentials[entry.Key] = entry.Value;
        }

        return CameraDiagnostics.TestAsync(request.Settings with
        {
            Sources = request.Settings.Sources.Select(c => c with { Enabled = true }).ToArray(),
            Credentials = credentials
        },
            request.Password ?? _passwords.GetValueOrDefault(request.Settings.ConfigurationId) ?? _password,
            request.Settings.Channels.First(), _ffmpeg, token);
    }

    public Task<OnvifDeviceReport> InspectSavedCameraAsync(string id, CancellationToken token)
    {
        var settings = _settings;
        var source = settings.Sources.FirstOrDefault(c => c.Id == id)
            ?? throw new ArgumentException("Save and activate this camera before using its saved ONVIF credentials.");
        if (source.OnvifAddress is null)
        {
            throw new ArgumentException("This camera has no ONVIF device address.");
        }

        var credentials = settings.Credentials.GetValueOrDefault(source.Id);
        return new OnvifClient().InspectAsync(source.OnvifAddress, credentials?.OnvifUsername ?? source.Username,
            credentials?.OnvifPassword ?? settings.CameraPassword(source.Channel, _password), token);
    }

    public async Task<LiveMediaSubscription> SubscribeLiveAsync(int channel, CancellationToken token,
        bool cameraVideo = false, bool compatibility = false)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (!Cameras.Any(camera => camera.State.Channel == channel))
            {
                throw new ArgumentException("Connect this camera before listening.");
            }

            var media = cameraVideo ? _cameraVideo : compatibility && !_mediaOptions.ForceTranscoding ? _compatibleLiveMedia : _liveMedia;
            return media.Subscribe(_settings, _password, channel,
                _batteryCameras.Get(_settings, _password, channel));
        }
        finally
        {
            _transition.Release();
        }
    }

    public object RewindSnapshot() => new
    {
        settings = _rewindSettings,
        generation = _generation,
        connected = Cameras.Length > 0,
        cameras = _liveMedia.RewindSnapshot()
    };

    public LiveRewindBuffer? RewindBuffer(int channel) => _liveMedia.RewindBuffer(channel);

    public async Task<RewindRecordingSelection> SelectRewindRecordingAsync(SaveRewindRecordingRequest request, CancellationToken token)
    {
        request.Validate();
        await _transition.WaitAsync(token);
        try
        {
            if (_generation != request.Generation || !_rewindSettings.Enabled || Cameras.Length == 0 || _connecting)
            {
                throw new InvalidOperationException("Playback changed. Select the recording again.");
            }
            return RewindRecordingSelection.Capture(request, _settings, RewindBuffer);
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task ConfigureRewindAsync(LiveRewindSettings settings, CancellationToken token)
    {
        if (settings.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        await _transition.WaitAsync(token);
        try
        {
            await _store.SettingsStore.UpdateAsync(document => document with { LiveRewind = settings }, token);
            _rewindSettings = settings;
            await ApplyRewindAsync();
        }
        finally
        {
            _transition.Release();
        }
    }

    private Task ApplyRewindAsync() => Cameras.Length == 0 ? Task.CompletedTask :
        _liveMedia.ConfigureRewindAsync(_settings, _password, _rewindSettings,
            channel => _batteryCameras.Get(_settings, _password, channel));

    public async Task<TalkbackCapability> TalkbackCapabilityAsync(int channel, CancellationToken token)
    {
        Task<TalkbackCapability> probe;
        await _transition.WaitAsync(token);
        try
        {
            var camera = Cameras.FirstOrDefault(camera => camera.State.Channel == channel)
                ?? throw new ArgumentException("Connect this camera before checking talkback.");
            if (!camera.State.Live && !_liveMedia.IsLive(channel) && !_compatibleLiveMedia.IsLive(channel) && !_cameraVideo.IsLive(channel))
            {
                return new(false, "waiting", "Talkback can be checked when the camera is sending live video.");
            }
            probe = _talkback.ProbeAsync(_settings, _password, channel, token);
        }
        finally
        {
            _transition.Release();
        }
        try
        {
            return await probe;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(false, "unavailable", "Camera connection changed; check talkback again.");
        }
    }

    public async Task<TalkbackLease> ReserveTalkbackAsync(int channel, CancellationToken token)
    {
        await _transition.WaitAsync(token);
        try
        {
            if (!Cameras.Any(camera => camera.State.Channel == channel && (camera.State.Live || _liveMedia.IsLive(channel) ||
                _compatibleLiveMedia.IsLive(channel) || _cameraVideo.IsLive(channel))))
            {
                throw new ArgumentException("This camera must be connected and sending live video before talking.");
            }
            return _talkback.Reserve(_settings, _password, channel, token);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task StopCamerasAsync()
    {
        await _talkback.StopAsync();
        await _liveMedia.StopAsync();
        await _compatibleLiveMedia.StopAsync();
        await _cameraVideo.StopAsync();
        var cameras = Interlocked.Exchange(ref _cameras, []);
        await Task.WhenAll(cameras.Select(async c => await c.DisposeAsync()));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync();
        await _transition.WaitAsync(CancellationToken.None);
        try
        {
            await StopCamerasAsync();
        }
        finally
        {
            _transition.Release();
        }
        await base.StopAsync(cancellationToken);
    }
}
