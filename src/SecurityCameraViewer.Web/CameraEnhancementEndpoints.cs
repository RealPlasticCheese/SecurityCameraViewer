namespace SecurityCameraViewer.Web;

public static class CameraEnhancementEndpoints
{
    public static void MapCameraEnhancementEndpoints(this WebApplication app)
    {
        app.MapPatch("/api/configurations/{configurationId}/cameras/{cameraId}/enhancements",
            async (string configurationId, string cameraId, CameraEnhancementUpdate update,
                CameraEnhancementStore store, CancellationToken cancellationToken) =>
                Results.Ok(await store.UpdateAsync(configurationId, cameraId, update, cancellationToken)));
    }
}
