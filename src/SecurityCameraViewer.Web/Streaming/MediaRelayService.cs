using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecurityCameraViewer.Web.Streaming;

public sealed record MediaRelayStatus(bool Running, string State, string Message, DateTimeOffset? StartedAt = null,
    int ReadyStreams = 0, int Readers = 0);

public interface IMediaRelayHealth
{
    Task<(int Streams, int Readers)> CheckAsync(int port, StreamingAccount account, CancellationToken token);
}

public sealed class MediaRelayHealth : IMediaRelayHealth, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<(int Streams, int Readers)> CheckAsync(int port, StreamingAccount account, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v3/paths/list?itemsPerPage=100");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.Username}:{account.Password}")));
        using var response = await _client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var items = data.RootElement.GetProperty("items").EnumerateArray().ToArray();
        return (items.Count(p => p.TryGetProperty("ready", out var ready) && ready.GetBoolean()),
            items.Sum(p => p.TryGetProperty("readers", out var readers) && readers.ValueKind == JsonValueKind.Array ? readers.GetArrayLength() : 0));
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Reconciles persisted settings with the active configuration and supervises the RTSP relay.</summary>
public sealed class MediaRelayService(StreamingSettingsStore settings, StreamingCameraCatalog catalog,
    IMediaRelayProcess process, IMediaRelayHealth health, ILogger<MediaRelayService> logger, RelayCameraInputState? inputState = null) : BackgroundService
{
    private MediaRelayStatus _status = new(false, "disabled", "RTSP & ONVIF service is disabled.");
    public MediaRelayStatus Status => Volatile.Read(ref _status);
    public StreamingAccount InternalAccount { get; } = new("scv-internal", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
    private string? _applied;
    private int _failures;
    private int _healthFailures;
    private DateTimeOffset _retryAt;
    private RelayCameraInputs? _inputs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    inputState?.Publish(null);
                    SetStatus(new(false, "retrying", "RTSP relay interrupted. Retrying automatically…"));
                    logger.LogWarning("RTSP relay supervision failed ({ErrorType}); retrying.", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopRelayAsync();
        }
    }

    public async Task ReconcileAsync(CancellationToken token)
    {
        var (current, account) = settings.Snapshot();
        if (!current.Enabled)
        {
            await StopRelayAsync();
            _applied = null;
            _failures = 0;
            SetStatus(new(false, "disabled", "RTSP & ONVIF service is disabled."));
            return;
        }

        var connection = catalog.Connection;
        var configuration = MediaMtxConfiguration.Create(current, account, catalog, InternalAccount, connection);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(current.MediaMtxExecutable + configuration)));
        if (_applied != fingerprint)
        {
            await StopRelayAsync();
            _applied = fingerprint;
            _retryAt = DateTimeOffset.MinValue;
            _failures = 0;
            _healthFailures = 0;
            _inputs = new(connection, current.RtspPort, InternalAccount);
        }

        if (!process.Running)
        {
            if (DateTimeOffset.UtcNow < _retryAt)
            {
                return;
            }

            try
            {
                await process.StopAsync();
                IntegrationSettings.WritePrivate(settings.RuntimePath, configuration);
                await process.StartAsync(current.MediaMtxExecutable, settings.RuntimePath, token);
                SetStatus(new(false, "starting", "Starting RTSP relay…"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await FailedAsync("MediaMTX could not start. Check the installation, ports and executable path.");
                return;
            }
        }

        try
        {
            var (streams, readers) = await health.CheckAsync(current.ApiPort, InternalAccount, token);
            _healthFailures = 0;
            _failures = 0;
            SetStatus(new(true, "running", "RTSP relay is ready. Camera feeds start when a client requests them.",
                Status.StartedAt ?? DateTimeOffset.UtcNow, streams, readers));
            inputState?.Publish(_inputs);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException ||
            ex is OperationCanceledException && !token.IsCancellationRequested)
        {
            if (++_healthFailures >= 5 || !process.Running)
            {
                await FailedAsync("RTSP relay is unavailable. Check MediaMTX and port conflicts; automatic retry is active.");
            }
            else
            {
                SetStatus(new(false, "starting", "Waiting for RTSP relay…"));
            }
        }
    }

    private async Task FailedAsync(string message)
    {
        await StopRelayAsync();
        _healthFailures = 0;
        _retryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(++_failures, 4))));
        SetStatus(new(false, "retrying", message));
        logger.LogWarning("RTSP relay unavailable; retrying at {RetryAt}.", _retryAt);
    }

    private void SetStatus(MediaRelayStatus status) => Volatile.Write(ref _status, status);

    private async Task StopRelayAsync()
    {
        inputState?.Publish(null);
        await process.StopAsync();
        try
        {
            File.Delete(settings.RuntimePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove private streaming runtime configuration. Check data directory permissions.");
        }
    }
}
