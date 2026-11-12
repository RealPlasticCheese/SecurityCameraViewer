using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record RecordingPlaybackFilter
{
    public int? Channel
    {
        get; init;
    }
    public DateOnly? Date
    {
        get; init;
    }
    public bool SavedReplayOnly
    {
        get; init;
    }
    public RecordingZoneSelection[] Zones { get; init; } = [];

    public string? Validate() => Channel is < 1
        ? "Choose a valid camera." : RecordingSearch.ValidateZones(Zones);
}

public sealed record SavedRecordingFilter(string Id, string Name, RecordingPlaybackFilter Filter);
public sealed record SaveRecordingFilterRequest(string? Id, string? Name, RecordingPlaybackFilter Filter);
