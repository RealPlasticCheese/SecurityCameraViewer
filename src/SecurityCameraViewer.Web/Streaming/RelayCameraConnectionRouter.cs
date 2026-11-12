using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web.Streaming;

public sealed record RelayCameraInputs(StreamingConnection Upstream, int Port, StreamingAccount Account);

/// <summary>Atomic routing readiness shared by consumers; it does not depend on the viewer or relay service.</summary>
public sealed class RelayCameraInputState
{
    private RelayCameraInputs? _current;
    private long _version;
    public RelayCameraInputs? Current => Volatile.Read(ref _current);
    public long Version => Interlocked.Read(ref _version);

    public void Publish(RelayCameraInputs? value)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _current, value), value))
        {
            Interlocked.Increment(ref _version);
        }
    }
}

public sealed class RelayCameraConnectionRouter(StreamingSettingsStore settings, RelayCameraInputState state) : ICameraConnectionRouter
{
    public CameraConnection Resolve(ViewerSettings connection, string password, int channel)
    {
        var current = state.Current;
        if (!settings.Current.Enabled || current is null || connection.ConfigurationId != current.Upstream.Settings.ConfigurationId ||
            !connection.Channels.Contains(channel) || !current.Upstream.Settings.Channels.Contains(channel))
        {
            return new(connection, password);
        }

        var upstream = current.Upstream.Settings with { Substream = connection.Substream };
        if (upstream.StreamUri(channel) != connection.StreamUri(channel) || upstream.CameraUsername(channel) != connection.CameraUsername(channel) ||
            upstream.CameraPassword(channel, current.Upstream.Password) != connection.CameraPassword(channel, password))
        {
            return new(connection, password);
        }

        var source = upstream.WithSources().Sources.Single(c => c.Channel == channel);
        var root = $"rtsp://127.0.0.1:{current.Port}/cameras/{source.Id}";
        var routed = source with
        {
            MainStream = root + "/main", SubStream = string.IsNullOrEmpty(source.SubStream) ? null : root + "/sub",
            Username = current.Account.Username, UseDefaultPassword = true
        };
        return new(connection with { Sources = [routed] }, current.Account.Password);
    }
}

/// <summary>Moves existing web and recording workers onto the relay when readiness changes.</summary>
public sealed class RelayViewerBridge(RelayCameraInputState state, ViewerService viewer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var version = state.Current is null ? state.Version : -1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (version != state.Version)
                {
                    var nextVersion = state.Version;
                    try
                    {
                        await viewer.RefreshMediaInputsAsync(stoppingToken);
                        version = nextVersion;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A failed camera transition must not stop HTTP or consume the change.
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }

                await Task.Delay(250, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
