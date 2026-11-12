using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Recording;

public sealed record RecordingZoneSelection(int Channel, string Id, string Name = "")
{
    [JsonIgnore]
    public bool IsValid => Channel >= 1 && !string.IsNullOrWhiteSpace(Id) &&
        Id.Length <= 128 && Name is not null && Name.Length <= 80;
}

public sealed record RecordingZoneGroup(int Channel, string CameraName, EventZone[] Zones);
public sealed record RecordingTimeline(RecordingEvent[] Recordings, bool Truncated);

/// <summary>Searches trigger metadata before paging the recording library.</summary>
public sealed record RecordingSearch
{
    public int? Channel
    {
        get; init;
    }
    public DateTimeOffset? Since
    {
        get; init;
    }
    public DateTimeOffset? Before
    {
        get; init;
    }
    public bool SavedReplayOnly { get; init; }
    public int Limit { get; init; } = 50;
    public RecordingZoneSelection[] Zones { get; init; } = [];

    public string? Validate()
    {
        if (Channel is < 1 || Limit is < 1 or > 100 || Since >= Before)
        {
            return "Choose a valid camera, date range and page size.";
        }

        return ValidateZones(Zones);
    }

    public static string? ValidateZones(RecordingZoneSelection[]? zones)
    {
        return zones is null || zones.Length > 1024 || zones.Any(zone => zone is null || !zone.IsValid) ||
            zones.DistinctBy(zone => (zone.Channel, zone.Id)).Count() != zones.Length
            ? "Select up to 1024 unique camera zones." : null;
    }
}
