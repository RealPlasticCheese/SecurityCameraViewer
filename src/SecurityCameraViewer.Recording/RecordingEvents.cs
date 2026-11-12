namespace SecurityCameraViewer.Recording;

public sealed record RecordingEvent
{
    public string ConfigurationId { get; init; } = "default";
    public string? SourceId
    {
        get; init;
    }
    public string CameraId => SourceId ?? $"camera-{Channel}";
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Channel
    {
        get; init;
    }
    public string CameraName { get; init; } = "";
    public DateTimeOffset StartedAt
    {
        get; init;
    }
    public DateTimeOffset TriggeredAt
    {
        get; init;
    }
    public DateTimeOffset? EndedAt
    {
        get; init;
    }
    public double DurationSeconds
    {
        get; init;
    }
    public long SizeBytes
    {
        get; init;
    }
    public bool Protected
    {
        get; init;
    }
    public bool Interrupted
    {
        get; init;
    }
    public RecordingTrigger[] AdditionalTriggers { get; init; } = [];
    public RecordingTrigger Trigger { get; init; } = new();
    public string Status { get; init; } = "recording";
    public string Mode { get; init; } = RecordingModes.Event;
    [System.Text.Json.Serialization.JsonIgnore]
    public string FileBasePath { get; init; } = "";
}

public interface IEventWriter : IAsyncDisposable
{
    string? Id => null;
    Task AddTriggerAsync(RecordingTrigger trigger, CancellationToken token) => Task.CompletedTask;
    Task AppendAsync(VideoFragment fragment, CancellationToken token);
    Task CompleteAsync(bool interrupted, CancellationToken token);
}

public interface IRecordingEventStore
{
    Task<IEventWriter> BeginTriggeredAsync(int channel, string name, byte[] initialization, double preSeconds,
        RecordingTrigger trigger, CancellationToken token, DateTimeOffset? triggeredAt = null) =>
        BeginAsync(channel, name, initialization, preSeconds, token, triggeredAt);
    Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization, double preSeconds, CancellationToken token, DateTimeOffset? triggeredAt = null);
}
