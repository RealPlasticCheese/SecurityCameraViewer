namespace SecurityCameraViewer.Recording;

public sealed record EventZone(string Id, string Name);
public sealed record CameraEvent(string Type, int? Channel = null, string? CameraName = null,
    EventZone[]? Zones = null, string? RecordingId = null, object? Data = null)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string[]? ActionIds
    {
        get; init;
    }
    public string? SourceId
    {
        get; init;
    }
    public string ConfigurationId { get; init; } = "default";
    public string? CameraId => SourceId ?? (Channel is { } channel ? $"camera-{channel}" : null);
}

/// <summary>Non-blocking publication. Delivery, persistence and transports belong to subscribers.</summary>
public interface ICameraEventPublisher
{
    void Publish(CameraEvent value);
}
public sealed class NullCameraEventPublisher : ICameraEventPublisher
{
    public static NullCameraEventPublisher Instance { get; } = new();
    public void Publish(CameraEvent value)
    {
    }
}

public sealed class SourceCameraEventPublisher(ICameraEventPublisher publisher, SecurityCameraViewer.Core.ViewerSettings settings) : ICameraEventPublisher
{
    public void Publish(CameraEvent value)
    {
        publisher.Publish(value with
        {
            ConfigurationId = settings.ConfigurationId,
            SourceId = value.Channel is { } channel ? settings.CameraId(channel) : null
        });
    }
}
public sealed record RecordingTrigger(string Source = "motion", string Reason = "Motion detected",
    string? RequestId = null, EventZone[]? Zones = null)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record ArmingOverride(string Mode, DateTimeOffset? ExpiresAt = null)
{
    public string EffectiveMode => ExpiresAt <= DateTimeOffset.UtcNow ? "schedule" : Mode;
}
