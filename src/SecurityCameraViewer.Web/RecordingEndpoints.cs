using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record ProtectionRequest(bool Protected);
public sealed record ActivateRecordingStorageRequest(string Directory, string ConfigurationId);

public static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recording", (RecordingService recording) => recording.Snapshot());
        app.MapGet("/api/detections", (RecordingService recording, CancellationToken token, DateTimeOffset? since) => recording.DetectionSnapshotAsync(token, since));
        app.MapGet("/api/recording/detection", (AiInferenceService inference) => inference.Catalog());
        app.MapPost("/api/recording/detection/reload", async (AiInferenceService inference, CancellationToken token) =>
        {
            await inference.ReloadModelsAsync(token);
            return inference.Catalog();
        });
        app.MapPost("/api/recording", async (RecordingSettings settings, RecordingService recording, AiInferenceService inference, CancellationToken token) =>
        {
            if (settings.Validate(inference.Registry) is { } error)
            {
                return Results.BadRequest(new
                {
                    message = error
                });
            }

            try
            {
                await recording.ConfigureAsync(settings, token);
                return Results.Ok();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new
                {
                    message = "Recording settings could not be applied. Check the recording folder, permissions and free space."
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new
                {
                    message = ex.Message
                });
            }
        });
        app.MapPost("/api/recording/storage/activate", async (ActivateRecordingStorageRequest request, RecordingService recording,
            CancellationToken token) =>
        {
            try
            {
                await recording.ActivateStorageAsync(request.Directory, request.ConfigurationId, token);
                return Results.Ok();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
        app.MapGet("/api/recordings", (int? channel, DateTimeOffset? before, int? limit, RecordingService recording) =>
            recording.Store.List(channel, before, limit ?? 50));
        app.MapPost("/api/recordings/search", (RecordingSearch search, RecordingService recording) => recording.Store.Search(search));
        app.MapGet("/api/recordings/timeline", (int channel, DateTimeOffset since, DateTimeOffset before, bool? savedReplayOnly, RecordingService recording) =>
            recording.Store.Timeline(channel, since, before, savedReplayOnly == true));
        app.MapGet("/api/recordings/{id}/markers", (string id, RecordingService recording) =>
            recording.Store.Find(id) is not null ? Results.Ok(recording.Store.Markers(id)) : Results.NotFound());
        app.MapGet("/api/recordings/zones", (RecordingService recording) => recording.Store.ZoneGroups());
        app.MapGet("/api/recordings/filters", (RecordingFilterStore filters, RecordingService recording, CancellationToken token) =>
            filters.ListAsync(recording.Connection.ConfigurationId, token));
        app.MapPost("/api/recordings/filters", (SaveRecordingFilterRequest request, RecordingFilterStore filters,
            RecordingService recording, CancellationToken token) => filters.SaveAsync(recording.Connection.ConfigurationId, request, token));
        app.MapGet("/api/recordings/{id}/video", (string id, bool? download, RecordingService recording) => Media(recording.Store, id, false, download == true));
        app.MapGet("/api/recordings/{id}/thumbnail", (string id, RecordingService recording) => Media(recording.Store, id, true, false));
        app.MapPost("/api/recordings/{id}/protect", async (string id, ProtectionRequest request, RecordingService recording, CancellationToken token) =>
            await recording.Store.ProtectAsync(id, request.Protected, token) ? Results.Ok() : Results.NotFound());
        app.MapDelete("/api/recordings/{id}", async (string id, RecordingService recording, CancellationToken token) =>
            await recording.Store.DeleteAsync(id, token) ? Results.Ok() : Results.Conflict(new
            {
                message = "Unprotect the recording first. Active events cannot be deleted."
            }));
        app.MapGet("/api/cameras/{channel:int}/snapshot", async (int channel, bool? refresh, HttpContext context,
            ViewerService viewer, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return viewer.Cameras.FirstOrDefault(c => c.State.Channel == channel) is { } camera &&
                await camera.SnapshotAsync(token, refresh == true) is { } frame
                ? Results.Bytes(frame.Jpeg, "image/jpeg") : Results.NotFound();
        });
    }

    public static IResult Media(FileRecordingStore store, string id, bool thumbnail, bool download)
    {
        var path = store.MediaPath(id, thumbnail);
        if (path is null)
        {
            return Results.NotFound();
        }

        try
        {
            // Retention can unlink a file without breaking a reader which already opened it.
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
            return Results.File(stream, thumbnail ? "image/jpeg" : "video/mp4", download ? $"camera-event-{id}.mp4" : null, enableRangeProcessing: !thumbnail);
        }
        catch (FileNotFoundException)
        {
            return Results.NotFound();
        }
    }
}
