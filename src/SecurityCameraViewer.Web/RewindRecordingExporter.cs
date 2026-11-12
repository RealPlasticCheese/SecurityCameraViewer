using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

/// <summary>Renders serially into temporary files, then admits the complete selection to the recording catalogue.</summary>
public sealed class RewindRecordingExporter(IRecordingClipRenderer renderer)
{
    private readonly SemaphoreSlim _admission = new(1, 1);

    public async Task<RecordingEvent[]> SaveSelectionAsync(SaveRewindRecordingRequest request, ViewerService viewer,
        RecordingService recording, CancellationToken token)
    {
        // Queue requests before snapshotting bytes so waiting saves cannot pin unlimited expired buffers.
        await _admission.WaitAsync(token);
        try
        {
            var selection = await viewer.SelectRewindRecordingAsync(request, token);
            return await recording.SaveRewindAsync(selection, this, token);
        }
        finally
        {
            _admission.Release();
        }
    }

    public async Task<RecordingEvent[]> SaveAsync(RewindRecordingSelection selection, FileRecordingStore store, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SecurityCameraViewer-rewind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var imports = new List<RecordingImport>();
            foreach (var camera in selection.Cameras)
            {
                foreach (var clip in camera.Clips)
                {
                    var recording = new RecordingEvent
                    {
                        ConfigurationId = selection.ConfigurationId,
                        SourceId = camera.CameraId,
                        Channel = camera.Channel,
                        CameraName = camera.Name,
                        StartedAt = clip.StartedAt,
                        TriggeredAt = clip.StartedAt,
                        EndedAt = clip.EndedAt,
                        DurationSeconds = clip.DurationSeconds,
                        Mode = RecordingModes.Event,
                        Trigger = new("replay", "Saved live replay")
                        {
                            Timestamp = clip.StartedAt
                        }
                    };
                    var path = Path.Combine(directory, recording.Id + ".mp4");
                    await renderer.RenderAsync(clip, path, token);
                    imports.Add(new(recording, path));
                }
            }
            return await store.ImportAsync([.. imports], token);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
