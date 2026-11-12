using System.Security.Cryptography;
using System.Text;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Web;

public sealed record ApiToken(string Id, string Name, string Hash, string[] Scopes, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);
public sealed record WebhookSubscription
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Webhook";
    public string Url { get; init; } = "";
    public string Secret { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string[] Events { get; init; } = [];
    public string[] CameraIds { get; init; } = [];
    public string[] ZoneIds { get; init; } = [];
    public int CooldownSeconds { get; init; } = 2;
}
public sealed record IntegrationConfiguration(bool Enabled = false, ApiToken[]? Tokens = null, WebhookSubscription[]? Webhooks = null);

/// <summary>Single atomic configuration owner. Token plaintext is never persisted.</summary>
public sealed class IntegrationSettings
{
    public static readonly string[] Scopes = ["read", "trigger", "control", "admin"];
    public static readonly string[] EventTypes = ["motion.started", "motion.ended", "recording.started", "recording.ready", "recording.failed", "camera.offline", "camera.online", "storage.warning", "storage.recovered"];
    private readonly Lock _gate = new();
    private readonly IAppSettingsStore _settingsStore;
    public string DirectoryPath
    {
        get;
    }
    public IntegrationConfiguration Current => _settingsStore.Read().Integrations;
    public IntegrationSettings(IAppSettingsStore settingsStore, IConfiguration configuration)
    {
        _settingsStore = settingsStore;
        DirectoryPath = Path.GetFullPath(configuration["integrations-directory"] ?? Path.Combine(Path.GetDirectoryName(settingsStore.FilePath)!, "integrations"));
    }
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public ApiToken? Authenticate(string value)
    {
        var hash = Hash(value);
        return Current.Tokens?.FirstOrDefault(t => (t.ExpiresAt is null || t.ExpiresAt > DateTimeOffset.UtcNow) &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(t.Hash), Encoding.ASCII.GetBytes(hash)));
    }
    public ApiToken? FindToken(string id) => Current.Tokens?.FirstOrDefault(t => t.Id == id && (t.ExpiresAt is null || t.ExpiresAt > DateTimeOffset.UtcNow));
    public (ApiToken Token, string Value) CreateToken(string name, string[] scopes, DateTimeOffset? expiresAt, bool setup = false)
    {
        lock (_gate)
        {
            if (setup && Current.Enabled)
            {
                throw new InvalidOperationException("Integrations are already enabled. Sign in with an administrator token.");
            }

            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || scopes is null || scopes.Length == 0 || scopes.Any(s => !Scopes.Contains(s)) ||
                expiresAt <= DateTimeOffset.UtcNow || (Current.Tokens?.Length ?? 0) >= 64)
            {
                throw new ArgumentException("Use a name, valid scopes and a future expiry (maximum 64 tokens).");
            }

            var value = "scv_" + NewSecret();
            var token = new ApiToken(Guid.NewGuid().ToString("N"), name, Hash(value), [.. scopes.Distinct()], DateTimeOffset.UtcNow, expiresAt);
            Save(Current with
            {
                Enabled = true,
                Tokens = [.. Current.Tokens ?? [], token]
            });
            return (token, value);
        }
    }
    public void Revoke(string id)
    {
        lock (_gate)
        {
            var tokens = Current.Tokens?.Where(t => t.Id != id).ToArray() ?? [];
            if (!tokens.Any(t => t.Scopes.Contains("admin") && (t.ExpiresAt is null || t.ExpiresAt > DateTimeOffset.UtcNow)))
            {
                throw new InvalidOperationException("Create another administrator token before revoking the last one.");
            }

            Save(Current with
            {
                Tokens = tokens
            });
        }
    }
    public WebhookSubscription SaveWebhook(WebhookSubscription value, bool rotate)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!Guid.TryParseExact(value.Id, "N", out _) || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 80 || value.Url.Length > 2048 ||
                !Uri.TryCreate(value.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Fragment.Length > 0 ||
                value.Events is null || value.Events.Length == 0 || value.Events.Any(e => !EventTypes.Contains(e)) ||
                value.CameraIds is null || value.CameraIds.Any(c => !RecordingTriggers.IsCameraId(c)) ||
                value.ZoneIds is null || value.ZoneIds.Length > 512 || value.ZoneIds.Any(z => z != "full-frame" && !Guid.TryParseExact(z, "N", out _)) ||
                value.CooldownSeconds is < 0 or > 3600)
            {
                throw new ArgumentException("Check webhook URL, name, events, camera/zone IDs and cooldown (0–3600 seconds).");
            }

            var previous = Current.Webhooks?.FirstOrDefault(w => w.Id == value.Id);
            if (previous is null && (Current.Webhooks?.Length ?? 0) >= 32)
            {
                throw new ArgumentException("Maximum 32 webhook destinations.");
            }

            value = value with
            {
                Secret = previous is null || rotate ? NewSecret() : previous.Secret
            };
            Save(Current with
            {
                Webhooks = [.. (Current.Webhooks ?? []).Where(w => w.Id != value.Id), value]
            });
            return value;
        }
    }
    public void DeleteWebhook(string id)
    {
        lock (_gate)
        {
            Save(Current with
            {
                Webhooks = Current.Webhooks?.Where(w => w.Id != id).ToArray()
            });
        }
    }
    private void Save(IntegrationConfiguration value)
    {
        _settingsStore.Update(settings => settings with { Integrations = value });
    }

    public static void WritePrivate(string path, string content) => PrivateFile.Write(path, content);
}
