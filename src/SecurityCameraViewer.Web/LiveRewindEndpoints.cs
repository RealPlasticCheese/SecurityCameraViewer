namespace SecurityCameraViewer.Web;

public static class LiveRewindEndpoints
{
    public static void MapLiveRewindEndpoints(this WebApplication app)
    {
        app.MapPost("/api/playback/recordings", async (SaveRewindRecordingRequest request, ViewerService viewer,
            RecordingService recording, RewindRecordingExporter exporter, CancellationToken token) =>
        {
            return Results.Ok(await exporter.SaveSelectionAsync(request, viewer, recording, token));
        });
        app.MapGet("/api/playback", (ViewerService viewer) => viewer.RewindSnapshot());
        app.MapPut("/api/playback", async (LiveRewindSettings settings, ViewerService viewer, CancellationToken token) =>
        {
            await viewer.ConfigureRewindAsync(settings, token);
            return Results.Ok(viewer.RewindSnapshot());
        });
        app.MapGet("/api/playback/{channel:int}/{epoch}", (int channel, string epoch, long at, ViewerService viewer) =>
        {
            if (at < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || at > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                return Results.BadRequest(new
                {
                    message = "Choose a valid playback timestamp."
                });
            }

            return viewer.RewindBuffer(channel)?.Playlist(epoch, at) is { } playlist ? Results.Ok(playlist) : Expired();
        });
        app.MapGet("/api/playback/{channel:int}/{epoch}/init", (int channel, string epoch, ViewerService viewer) =>
            Media(viewer.RewindBuffer(channel), epoch, null));
        app.MapGet("/api/playback/{channel:int}/{epoch}/fragments/{sequence:long}",
            (int channel, string epoch, long sequence, ViewerService viewer) => Media(viewer.RewindBuffer(channel), epoch, sequence));
        app.MapGet("/api/playback/{channel:int}/{epoch}/window", (int channel, string epoch, long at, ViewerService viewer) =>
        {
            if (at < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() || at > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                return Results.BadRequest(new
                {
                    message = "Choose a valid playback timestamp."
                });
            }
            return viewer.RewindBuffer(channel)?.Window(epoch, at) is { } window
                ? (IResult)new RewindPlaybackWindowResult(window) : Expired();
        });
    }

    public static IResult Media(LiveRewindBuffer? buffer, string epoch, long? sequence) =>
        buffer?.Media(epoch, sequence) is { } bytes ? Results.Bytes(bytes, "video/mp4") : Expired();

    private static IResult Expired() => Results.Json(new { message = "Footage is unavailable or has left the rewind buffer." }, statusCode: 410);
}
