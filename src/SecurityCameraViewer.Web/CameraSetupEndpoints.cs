using System.Text;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web;

public sealed record DiscoveryRequest(string? InterfaceAddress);
public sealed record OnvifRequest(string Address, string Username, string Password);
public sealed record DiagnosticRequest(int Channel, bool Substream = false);

public static class CameraSetupEndpoints
{
    public static void MapCameraSetupEndpoints(this WebApplication app)
    {
        var onvif = new OnvifClient();
        var setup = app.MapGroup("/api/setup");
        setup.MapGet("/configurations", (ViewerService viewer) => viewer.Configurations());
        setup.MapGet("/configurations/{id}/passwords", (string id, ViewerService viewer, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return viewer.ConfigurationPasswords(id);
        });
        setup.MapPost("/configurations", async (ConnectionRequest request, ViewerService viewer) =>
        {
            if (request.Settings is null)
            {
                throw new ArgumentException("Configuration settings are required.");
            }

            await viewer.ConfigureAsync(request);
            return Results.Ok();
        });
        setup.MapPost("/configurations/{id}/activate", async (string id, ViewerService viewer) =>
        {
            await viewer.ActivateAsync(id);
            return Results.Ok();
        });
        setup.MapDelete("/configurations/{id}", async (string id, ViewerService viewer) =>
        {
            await viewer.DeleteConfigurationAsync(id);
            return Results.Ok();
        });
        setup.MapGet("/interfaces", () => OnvifClient.NetworkAddresses());
        setup.MapPost("/discover", async (DiscoveryRequest request, CancellationToken token) => await onvif.DiscoverAsync(request.InterfaceAddress, token));
        setup.MapPost("/onvif", async (OnvifRequest request, CancellationToken token) =>
        {
            if (request.Username is null || request.Username.Length > 256 || request.Password is null || request.Password.Length > 4096)
            {
                throw new ArgumentException("Enter valid ONVIF credentials.");
            }

            return await onvif.InspectAsync(request.Address, request.Username, request.Password, token);
        });
        setup.MapPost("/onvif/{id}", async (string id, ViewerService viewer, CancellationToken token) =>
            await viewer.InspectSavedCameraAsync(id, token));
        setup.MapPost("/test", async (DiagnosticRequest request, ViewerService viewer, CancellationToken token) =>
            await viewer.DiagnoseAsync(request.Channel, request.Substream, token));
        setup.MapPost("/preview", async (ConnectionRequest request, ViewerService viewer, CancellationToken token) =>
            await viewer.PreviewAsync(request, token));
        setup.MapPost("/export", (System.Text.Json.JsonElement report) =>
            Results.File(Encoding.UTF8.GetBytes(CameraDiagnostics.Export(report)), "application/json", "camera-setup-report.json"));
        setup.MapGet("/metadata", (ViewerService viewer, bool includePasswords = false) => Results.Text(viewer.Metadata(includePasswords), "application/json", Encoding.UTF8));
        setup.MapGet("/metadata/export", (ViewerService viewer, bool includePasswords = false) => Results.File(Encoding.UTF8.GetBytes(viewer.Metadata(includePasswords)), "application/json", "camera-diagnostics.json"));
    }
}
