using System.Text.Json;

namespace SecurityCameraViewer.Recording;

/// <summary>
/// A reusable action selected by camera defaults or individual detection zones.
/// </summary>
public sealed record MotionAction
{
    public const string DefaultRecordingId = "record";
    public const string RecordType = "record";
    public const string WebhookType = "webhook";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Action";
    public string Type { get; init; } = RecordType;
    public bool Enabled { get; init; } = true;
    public string Url { get; init; } = "";
    public string? Payload
    {
        get; init;
    }
    public int CooldownSeconds { get; init; } = 30;

    public static MotionAction DefaultRecording => new()
    {
        Id = DefaultRecordingId,
        Name = "Record"
    };

    public string? Validate()
    {
        if ((Id != DefaultRecordingId && !Guid.TryParseExact(Id, "N", out _)) ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 80 ||
            Type is not (RecordType or WebhookType) ||
            (Id == DefaultRecordingId && Type != RecordType))
        {
            return "Actions need a unique ID, a name of up to 80 characters, and a Record or Webhook type.";
        }

        if (CooldownSeconds is < 0 or > 3600)
        {
            return "Webhook cooldown must be between 0 and 3600 seconds.";
        }

        if (Type == RecordType)
        {
            return null;
        }

        if (Url is null || Url.Length > 2048 ||
            !Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            return "Webhook actions need an HTTP or HTTPS URL without embedded credentials or a fragment.";
        }

        if (Payload?.Length > 16384)
        {
            return "Webhook JSON bodies must be at most 16384 characters.";
        }

        if (!string.IsNullOrWhiteSpace(Payload))
        {
            try
            {
                using var document = JsonDocument.Parse(Payload);
            }
            catch (JsonException)
            {
                return "Enter valid JSON for the webhook body, or leave it empty to send the motion event.";
            }
        }

        return null;
    }
}

public interface IMotionActionCatalog
{
    MotionAction? FindAction(string id);
}

/// <summary>
/// Resolves actions without making recording depend on delivery or action order.
/// A missing zone selection inherits camera defaults; an empty selection does nothing.
/// </summary>
public sealed class MotionActionRouter(RecordingSettings settings, CameraRecordingSettings camera)
{
    private readonly Dictionary<string, MotionAction> _actions = settings.Actions.ToDictionary(action => action.Id);
    private readonly Dictionary<string, MotionZone> _zones = camera.Zones.ToDictionary(zone => zone.Id);

    public MotionAction[] ForZone(string zoneId)
    {
        var selected = _zones.TryGetValue(zoneId, out var zone)
            ? zone.ActionIds ?? camera.DefaultActionIds
            : camera.DefaultActionIds;

        return [.. selected
            .Select(id => _actions.GetValueOrDefault(id))
            .OfType<MotionAction>()
            .Where(action => action.Enabled)];
    }

    public bool Records(string zoneId)
    {
        return ForZone(zoneId).Any(action => action.Type == MotionAction.RecordType);
    }
}
