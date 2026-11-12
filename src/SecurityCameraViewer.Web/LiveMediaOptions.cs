namespace SecurityCameraViewer.Web;

/// <summary>Compatibility and memory budgets for live browser media.</summary>
public sealed record LiveMediaOptions
{
    public bool ForceTranscoding
    {
        get; init;
    }
    public int MaximumQueuedBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumQueuedPackets { get; init; } = 8;

    public void Validate()
    {
        if (MaximumQueuedBytes <= 0 || MaximumQueuedPackets <= 0)
        {
            throw new ArgumentException("Live media queue budgets must be positive.");
        }
    }
}
