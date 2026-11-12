namespace SecurityCameraViewer.Recording;

public sealed record RecordingCatalogTotals(long Bytes, long ReservedBytes, int Count);

/// <summary>Durable metadata and indexed queries. Media files and capture state belong to the recording store.</summary>
public interface IRecordingCatalog
{
    bool Exists
    {
        get;
    }
    void Initialize();
    RecordingEvent? Find(string id);
    RecordingEvent[] Search(RecordingSearch search, string? cameraId = null, string? status = null, bool oldestFirst = false, bool? protectedOnly = null);
    RecordingEvent[] FindByRequest(string requestId);
    RecordingZoneGroup[] ZoneGroups();
    object[] Cameras();
    RecordingCatalogTotals Totals();
    void Save(RecordingEvent recording, RecordingTrigger[]? markers = null);
    void AddMarker(string recordingId, RecordingTrigger trigger);
    RecordingTrigger[] Markers(string recordingId, bool all = false);
    RecordingEvent[] CompletedAfter(RecordingEvent? cursor, int limit);
    bool WasPruned(string id);
    void RememberPruned(RecordingEvent recording);
    RecordingTimeline Timeline(int? channel, DateTimeOffset since, DateTimeOffset before, string? cameraId = null, bool savedReplayOnly = false);
    void Delete(string id, RecordingEvent? pruned = null);
}
