using SecurityCameraViewer.Nvr;

namespace SecurityCameraViewer.Web.Nvr;

public static class NvrEndpoints
{
    public static void MapNvrEndpoints(this WebApplication app)
    {
        app.MapGet("/api/nvr/settings", (NvrSettingsStore settings) =>
        {
            var current = settings.Current;
            return new
            {
                current.Host,
                current.Port,
                current.Username,
                settings.HasPassword,
                settings.Notice
            };
        });
        app.MapPut("/api/nvr/settings", (NvrSettingsRequest request, NvrSettingsStore settings) =>
        {
            settings.Save(request);
            return Results.Ok();
        });
        app.MapGet("/api/nvr/device", async (INvrClient client, NvrSettingsStore settings, CancellationToken token) =>
            await client.GetDeviceInfoAsync(settings.GetConnection(), token));
        app.MapGet("/api/nvr/storage", async (INvrStorageClient client, NvrSettingsStore settings, CancellationToken token) =>
            await client.GetStorageInfoAsync(settings.GetConnection(), token));
        app.MapPost("/api/nvr/search", async (NvrRecordingRequest request, INvrClient client, NvrSettingsStore settings, CancellationToken token) =>
            await client.SearchAsync(settings.GetConnection(), request, token));
        app.MapGet("/api/nvr/exports", (NvrExportStore store, NvrExportService exports) => new { Saved = store.List(), Jobs = exports.Jobs });
        app.MapPost("/api/nvr/exports", (NvrRecordingRequest request, NvrExportService exports) =>
        {
            var job = exports.Start(request);
            return Results.Accepted($"/api/nvr/exports/{job.Id}", job);
        });
        app.MapGet("/api/nvr/exports/{id}", (string id, NvrExportService exports) =>
            exports.Jobs.FirstOrDefault(j => j.Id == id) is { } job ? Results.Ok(job) : Results.NotFound());
        app.MapDelete("/api/nvr/exports/{id}", (string id, NvrExportStore store) => store.Delete(id) ? Results.Ok() : Results.NotFound());
        app.MapDelete("/api/nvr/exports/{id}/message", (string id, NvrExportService exports) => exports.DeleteMessage(id) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/nvr/exports/{id}/cancel", (string id, NvrExportService exports) => exports.Cancel(id) ? Results.Ok() : Results.NotFound());
        app.MapGet("/api/nvr/exports/{id}/video", (string id, bool? download, NvrExportStore store) =>
        {
            var saved = store.List().FirstOrDefault(e => e.Id == id);
            var path = store.FindVideo(id);
            return saved is null || path is null ? Results.NotFound() : Results.File(path, "video/mp4",
                download == true ? $"nvr-channel-{saved.Recording.Channel}-{saved.Recording.Begin:yyyyMMdd-HHmmss}.mp4" : null, enableRangeProcessing: true);
        });
    }
}
