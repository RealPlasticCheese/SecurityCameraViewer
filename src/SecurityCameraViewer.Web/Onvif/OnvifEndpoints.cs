using System.Xml;
using System.Xml.Linq;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Web.Onvif;

public static class OnvifEndpoints
{
    public static void MapOnvifEndpoints(this WebApplication app)
    {
        foreach (var prefix in new[] { "/onvif", "/onvif/cameras/{cameraId}" })
        {
            foreach (var service in new[] { "device_service", "media_service", "media2_service", "events_service", "deviceio_service" })
            {
                var name = service;
                app.MapPost($"{prefix}/{service}", (HttpContext context, StreamingSettingsStore settings, StreamingCameraCatalog catalog,
                    OnvifAuthentication auth, OnvifDeviceService device, OnvifMediaService media, OnvifEventService events) =>
                    SoapAsync(context, settings, catalog, auth, device, media, events, name));
            }

            app.MapPost($"{prefix}/subscriptions/{{subscriptionId}}", (HttpContext context, StreamingSettingsStore settings,
                StreamingCameraCatalog catalog, OnvifAuthentication auth, OnvifDeviceService device, OnvifMediaService media, OnvifEventService events) =>
                SoapAsync(context, settings, catalog, auth, device, media, events, "subscription"));

            app.MapGet($"{prefix}/snapshot/{{profileToken}}", SnapshotAsync);
        }
    }

    private static bool Available(HttpContext context, StreamingSettingsStore settings, StreamingCameraCatalog catalog)
    {
        var cameraId = context.Request.RouteValues["cameraId"]?.ToString();
        if (!settings.Current.Enabled || !settings.Current.OnvifEnabled || cameraId is not null && !catalog.Cameras.Any(c => c.Id == cameraId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        return true;
    }

    private static async Task SoapAsync(HttpContext context, StreamingSettingsStore settings, StreamingCameraCatalog catalog,
        OnvifAuthentication auth, OnvifDeviceService device, OnvifMediaService media, OnvifEventService events, string service)
    {
        if (!Available(context, settings, catalog))
        {
            return;
        }

        OnvifRequest? request = null;
        try
        {
            request = await ReadAsync(context.Request.Body, context.RequestAborted);
            var operation = request.Operation;
            var clock = service == "device_service" && operation.Name == Device + "GetSystemDateAndTime";
            if (!clock && !auth.Authenticate(context, request))
            {
                auth.Challenge(context);
                await WriteAsync(context, request, Fault(request, new("ter:NotAuthorized", "ter:NotAuthorized", "Client credentials are required.")), 401);
                return;
            }

            var cameraId = context.Request.RouteValues["cameraId"]?.ToString();
            var subscriptionId = context.Request.RouteValues["subscriptionId"]?.ToString();
            var ns = operation.Name.Namespace;
            XElement response = service switch
            {
                "device_service" when ns == Device => device.Handle(operation, cameraId),
                "deviceio_service" when ns == DeviceIo => device.Handle(operation, cameraId),
                "media_service" when ns == Media => media.Handle(operation, cameraId),
                "media2_service" when ns == Media2 => media.Handle(operation, cameraId),
                "events_service" when ns == Events => await events.HandleAsync(operation, cameraId, null, context.RequestAborted),
                "subscription" when ns == Events || ns == Notification => await events.HandleAsync(operation, cameraId, subscriptionId, context.RequestAborted),
                _ => throw Unsupported()
            };
            await WriteAsync(context, request, Envelope(request, response, ResponseAction(response)));
        }
        catch (OnvifFault fault)
        {
            await WriteAsync(context, request, Fault(request, fault), 400);
        }
        catch (XmlException)
        {
            await WriteAsync(context, request, Fault(request, new("ter:InvalidArgVal", "ter:InvalidMessage", "Invalid XML message.")), 400);
        }
    }

    private static async Task SnapshotAsync(HttpContext context, StreamingSettingsStore settings, StreamingCameraCatalog catalog,
        OnvifAuthentication auth, IStreamingSnapshotProvider snapshots)
    {
        if (!Available(context, settings, catalog))
        {
            return;
        }

        if (!auth.Authenticate(context))
        {
            auth.Challenge(context);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        try
        {
            var profile = catalog.RequireProfile(context.Request.RouteValues["profileToken"]?.ToString(), context.Request.RouteValues["cameraId"]?.ToString());
            var jpeg = await snapshots.GetAsync(profile, context.RequestAborted);
            context.Response.ContentType = "image/jpeg";
            await context.Response.Body.WriteAsync(jpeg, context.RequestAborted);
        }
        catch (OnvifFault)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }
}
