using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record WebhookDelivery(string Id, string SubscriptionId, string EventId, string EventType, string Payload,
    DateTimeOffset CreatedAt, DateTimeOffset NextAttempt, int Attempts = 0, string Status = "pending", int? HttpStatus = null, string? Error = null)
{
    public string? ActionId
    {
        get; init;
    }
    public string? ActionName
    {
        get; init;
    }
    public string? CooldownKey
    {
        get; init;
    }
    public DateTimeOffset? CooldownUntil
    {
        get; init;
    }
}

/// <summary>Local durable outbox and bounded dispatcher. No broker, database or disk writes during idle polling.</summary>
public sealed class WebhookDispatcher : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IntegrationSettings _settings;
    private readonly IntegrationEventHub _hub;
    private readonly IMotionActionCatalog? _actions;
    private readonly ConcurrentDictionary<string, WebhookDelivery> _deliveries = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _persistenceFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private string QueueDirectory => Path.Combine(_settings.DirectoryPath, "deliveries");
    public string Notice { get; private set; } = "";
    public int PendingCount => _deliveries.Values.Count(d => d.Status == "pending");
    public int FailedCount => _deliveries.Values.Count(d => d.Status == "failed");
    public WebhookDelivery[] List() => [.. _deliveries.Values.OrderByDescending(d => d.CreatedAt).Take(100)];
    public WebhookDispatcher(IntegrationSettings settings, IntegrationEventHub hub, IMotionActionCatalog? actions = null)
    {
        _settings = settings;
        _hub = hub;
        _actions = actions;
        if (!Directory.Exists(QueueDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(QueueDirectory, "*.json").Take(2200))
        {
            try
            {
                var item = JsonSerializer.Deserialize<WebhookDelivery>(File.ReadAllText(path), Json);
                if (item is not null && Guid.TryParseExact(item.Id, "N", out _) && Path.GetFileNameWithoutExtension(path) == item.Id)
                {
                    _deliveries[item.Id] = item;
                    if (item.CooldownKey is { } key && item.CooldownUntil is { } until &&
                        until > _cooldowns.GetValueOrDefault(key))
                    {
                        _cooldowns[key] = until;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                Notice = "A delivery file could not be read; inspect the integrations folder.";
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(PersistEvents(stoppingToken), Dispatch(stoppingToken));
    }

    private async Task PersistEvents(CancellationToken token)
    {
        try
        {
            await foreach (var value in _hub.Pending.Reader.ReadAllAsync(token))
            {
                foreach (var hook in _settings.Current.Webhooks ?? [])
                {
                    if (Matches(hook, value))
                    {
                        await Enqueue(hook, value, token);
                    }
                }

                if (value.Type == "motion.started")
                {
                    foreach (var id in value.ActionIds ?? [])
                    {
                        if (_actions?.FindAction(id) is { Enabled: true, Type: MotionAction.WebhookType } action)
                        {
                            await EnqueueCoreAsync(Subscription(action), value, token, action: action);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _persistenceFinished.TrySetResult();
        }
    }

    public static bool Matches(WebhookSubscription hook, CameraEvent value) => hook.Enabled && hook.Events.Contains(value.Type) &&
        (hook.CameraIds.Length == 0 || value.CameraId is not null && hook.CameraIds.Contains(value.CameraId)) &&
        (hook.ZoneIds.Length == 0 || value.Zones?.Any(z => hook.ZoneIds.Contains(z.Id)) == true);
    public Task<string?> Enqueue(WebhookSubscription hook, CameraEvent value, CancellationToken token, bool test = false)
    {
        return EnqueueCoreAsync(hook, value, token, test);
    }

    private async Task<string?> EnqueueCoreAsync(
        WebhookSubscription hook,
        CameraEvent value,
        CancellationToken token,
        bool test = false,
        MotionAction? action = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            var key = $"{hook.Id}:{value.Type}:{value.CameraId}:{string.Join(',', (value.Zones ?? []).Select(z => z.Id).Order())}";
            if (!test && _cooldowns.TryGetValue(key, out var until) && value.Timestamp < until)
            {
                return null;
            }

            if (_deliveries.Values.Any(d => d.SubscriptionId == hook.Id && d.EventId == value.Id))
            {
                return null;
            }

            if (_deliveries.Values.Count(d => d.Status is "pending" or "failed") >= 2000 || _deliveries.Values.Sum(d => (long)Encoding.UTF8.GetByteCount(d.Payload)) >= 32L * 1024 * 1024)
            {
                Notice = "Webhook queue is full. Retry or dismiss failed deliveries.";
                return null;
            }
            var payload = string.IsNullOrWhiteSpace(action?.Payload)
                ? JsonSerializer.Serialize(value, Json)
                : action.Payload;
            var item = new WebhookDelivery(Guid.NewGuid().ToString("N"), hook.Id, value.Id, value.Type, payload, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            {
                ActionId = action?.Id,
                ActionName = action?.Name,
                CooldownKey = key,
                CooldownUntil = value.Timestamp.AddSeconds(hook.CooldownSeconds)
            };
            Save(item);
            _cooldowns[key] = item.CooldownUntil.Value;
            if (_cooldowns.Count > 4096)
            {
                foreach (var old in _cooldowns.Where(p => p.Value < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray())
                {
                    _cooldowns.Remove(old);
                }
            }

            TrimHistory();
            return item.Id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Webhook queue could not be saved. Check free space and permissions.";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task Dispatch(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // One in-flight request per subscription; four destinations may deliver concurrently.
                var due = _deliveries.Values.Where(d => d.Status == "pending").GroupBy(d => d.SubscriptionId)
                    .Select(g => g.OrderBy(d => d.CreatedAt).First()).Where(d => d.NextAttempt <= DateTimeOffset.UtcNow && Destination(d)?.Enabled != false).Take(4).ToArray();
                await Task.WhenAll(due.Select(d => Deliver(d, token)));
                await Task.Delay(250, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task Deliver(WebhookDelivery item, CancellationToken token)
    {
        var hook = Destination(item);
        // Paused subscriptions retain pending work. Deletion cancels it.
        if (hook is { Enabled: false })
        {
            return;
        }

        var next = item;
        if (hook is null)
        {
            next = item with
            {
                Status = "cancelled",
                Error = "Webhook or action deleted"
            };
        }
        else
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(item.Payload, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-SCV-Event", item.EventType);
            request.Headers.Add("X-SCV-Delivery", item.Id);
            request.Headers.Add("X-SCV-Timestamp", timestamp);
            if (hook.Secret.Length > 0)
            {
                var signature = HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(hook.Secret),
                    Encoding.UTF8.GetBytes(timestamp + "." + item.Payload));
                request.Headers.Add("X-SCV-Signature", "sha256=" + Convert.ToHexStringLower(signature));
            }

            if (item.ActionId is not null)
            {
                request.Headers.Add("X-SCV-Action", item.ActionId);
            }
            int? status = null;
            string? error = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    error = $"HTTP {status}";
                }

                retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                error = "Connection failed or timed out";
            }
            var attempts = item.Attempts + 1;
            var retryable = status is null or 408 or 425 or 429 || status >= 500;
            var delay = Math.Clamp(retryAfter?.TotalSeconds ?? Math.Pow(2, attempts) + Random.Shared.NextDouble() * 2, 2, 3600);
            next = item with
            {
                Attempts = attempts,
                HttpStatus = status,
                Error = error,
                Status = error is null ? "delivered" : retryable && attempts < 10 ? "pending" : "failed",
                NextAttempt = DateTimeOffset.UtcNow.AddSeconds(delay)
            };
        }
        await _gate.WaitAsync(token);
        try
        {
            // An administrator may dismiss a delivery while the HTTP call is in flight.
            if (_deliveries.TryGetValue(item.Id, out var current) && current == item)
            {
                Save(next);
            }

            TrimHistory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "Delivery result could not be saved; it may be sent again after recovery.";
            _deliveries.TryUpdate(item.Id, item with
            {
                NextAttempt = DateTimeOffset.UtcNow.AddMinutes(1)
            }, item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> Update(string id, bool retry, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!_deliveries.TryGetValue(id, out var item))
            {
                return false;
            }

            Save(item with
            {
                Status = retry ? "pending" : "cancelled",
                Attempts = retry ? 0 : item.Attempts,
                NextAttempt = DateTimeOffset.UtcNow,
                Error = null
            });
            TrimHistory();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WebhookSubscription? Destination(WebhookDelivery item)
    {
        if (item.ActionId is null)
        {
            return _settings.Current.Webhooks?.FirstOrDefault(hook => hook.Id == item.SubscriptionId);
        }

        return _actions?.FindAction(item.ActionId) is { Type: MotionAction.WebhookType } action
            ? Subscription(action)
            : null;
    }

    private static WebhookSubscription Subscription(MotionAction action)
    {
        return new WebhookSubscription
        {
            Id = "action:" + action.Id,
            Name = action.Name,
            Url = action.Url,
            Enabled = action.Enabled,
            CooldownSeconds = action.CooldownSeconds
        };
    }

    private void Save(WebhookDelivery item)
    {
        IntegrationSettings.WritePrivate(Path.Combine(QueueDirectory, item.Id + ".json"), JsonSerializer.Serialize(item, Json));
        _deliveries[item.Id] = item;
    }

    private void TrimHistory()
    {
        foreach (var old in _deliveries.Values.Where(d => d.Status is "delivered" or "cancelled").OrderByDescending(d => d.CreatedAt).Skip(100))
        {
            File.Delete(Path.Combine(QueueDirectory, old.Id + ".json"));
            _deliveries.TryRemove(old.Id, out _);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Registered before recording: the host stops capture first, then drains event admission here.
        _hub.Pending.Writer.TryComplete();
        try
        {
            await _persistenceFinished.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
        base.Dispose();
    }
}
