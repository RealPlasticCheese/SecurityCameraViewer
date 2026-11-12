using SecurityCameraViewer.Web.Onvif;

namespace SecurityCameraViewer.Web.Streaming;

public static class StreamingEndpoints
{
    public static void MapStreamingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/streaming", (StreamingSettingsStore store, StreamingCameraCatalog catalog,
            MediaRelayService relay, OnvifDiscoveryService discovery) => Results.Ok(new
            {
                settings = store.Current, hasPassword = store.Account.Password.Length > 0,
                status = relay.Status, discovery = discovery.Status,
                aggregateAddress = store.Current.OnvifBaseUrl.Length > 0 ? catalog.ServiceUri("device_service", null).AbsoluteUri : "",
                cameras = catalog.Cameras.Select(c => new
                {
                    c.Id, c.Name, c.MotionActivated,
                    onvifAddress = store.Current.OnvifBaseUrl.Length > 0 ? catalog.ServiceUri("device_service", c.Id).AbsoluteUri : "",
                    profiles = catalog.Profiles(c.Id).Select(p => new
                    {
                        p.Token, p.Substream, p.Media,
                        rtspAddress = store.Current.AdvertisedHost.Length > 0 ? catalog.StreamUri(p).AbsoluteUri : ""
                    })
                })
            }));
        app.MapPut("/api/streaming", (StreamingSettingsRequest request, StreamingSettingsStore store) =>
        {
            store.Save(request);
            return Results.Ok(new { message = "Settings saved. The RTSP & ONVIF service will apply them within a few seconds." });
        });
        app.MapPost("/api/streaming/refresh-media", (StreamingMetadataService metadata) =>
        {
            metadata.Refresh();
            return Results.Ok(new { message = "Media metadata refresh queued. Motion-activated cameras are not polled; use profile overrides for them." });
        });
    }

    public static void AddStreamingServices(this IServiceCollection services)
    {
        services.AddSingleton<StreamingSettingsStore>();
        services.AddSingleton<RelayCameraInputState>();
        services.AddSingleton<Core.ICameraConnectionRouter, RelayCameraConnectionRouter>();
        services.AddSingleton<IStreamingCameraProvider>(s => s.GetRequiredService<ViewerService>());
        services.AddSingleton<StreamingCameraCatalog>();
        services.AddSingleton<IMediaRelayProcess, MediaMtxProcess>();
        services.AddSingleton<IMediaRelayHealth, MediaRelayHealth>();
        services.AddSingleton<MediaRelayService>();
        services.AddHostedService(s => s.GetRequiredService<MediaRelayService>());
        services.AddSingleton<IStreamingSnapshotProvider, StreamingSnapshots>();
        services.AddSingleton<IStreamingMediaProbe, StreamingMediaProbe>();
        services.AddSingleton<StreamingMetadataService>();
        services.AddHostedService(s => s.GetRequiredService<StreamingMetadataService>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OnvifAuthentication>();
        services.AddSingleton<OnvifDeviceService>();
        services.AddSingleton<OnvifMediaService>();
        services.AddSingleton<OnvifEventService>();
        services.AddHostedService(s => s.GetRequiredService<OnvifEventService>());
        services.AddSingleton<OnvifDiscoveryResponder>();
        services.AddSingleton<OnvifDiscoveryService>();
        services.AddHostedService(s => s.GetRequiredService<OnvifDiscoveryService>());
        services.AddHostedService<RelayViewerBridge>();
    }
}
