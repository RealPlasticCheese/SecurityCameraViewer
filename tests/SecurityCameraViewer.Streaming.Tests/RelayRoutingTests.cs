using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class RelayRoutingTests
{
    public static Task RunAsync()
    {
        var fixture = Create();
        var state = new RelayCameraInputState();
        var router = new RelayCameraConnectionRouter(fixture.Store, state);
        var connection = fixture.Provider.StreamingConnection;
        Check(router.Resolve(connection.Settings, connection.Password, 1).Settings == connection.Settings,
            "Media inputs remain direct before the relay is ready");
        var inputs = new RelayCameraInputs(connection, 18554, new("scv-internal", "internal-secret"));
        state.Publish(inputs);
        Check(state.Version == 1, "Routing state publishes a version when relay readiness changes");
        var routed = router.Resolve(connection.Settings, connection.Password, 1);
        var cameraId = connection.Settings.Sources[0].Id;
        Check(routed.Settings.StreamUri(1).AbsoluteUri == $"rtsp://127.0.0.1:18554/cameras/{cameraId}/main" &&
            routed.Settings.CameraUsername(1) == "scv-internal" && routed.Settings.CameraPassword(1, routed.Password) == "internal-secret",
            "Web and recording inputs share the local main stream using internal credentials");
        var substream = router.Resolve(connection.Settings with { Substream = true }, connection.Password, 1);
        Check(substream.Settings.StreamUri(1).AbsoluteUri.EndsWith("/sub"), "Grid substreams share the independent local substream path");
        var noSub = router.Resolve(connection.Settings with { Substream = true }, connection.Password, 3);
        Check(noSub.Settings.StreamUri(3).AbsoluteUri.EndsWith("/main"), "A camera without a substream still uses its main relay path");
        var other = connection.Settings with { ConfigurationId = "other" };
        Check(router.Resolve(other, connection.Password, 1).Settings == other,
            "Routing never redirects a different camera configuration");
        var changed = connection.Settings with { Sources = [connection.Settings.Sources[0] with { MainStream = "rtsp://192.0.2.90/new" }] };
        Check(router.Resolve(changed, connection.Password, 1).Settings == changed, "Changed upstream URLs are not routed through stale relay paths");
        Check(router.Resolve(connection.Settings, "new-upstream-password", 1).Password == "new-upstream-password",
            "Changed upstream credentials do not reuse an old relay connection");
        state.Publish(inputs);
        Check(state.Version == 1, "Unchanged readiness does not restart consumers on every health poll");
        state.Publish(null);
        Check(state.Version == 2 && router.Resolve(connection.Settings, connection.Password, 1).Settings == connection.Settings,
            "Relay failure restores direct inputs for recovery");
        state.Publish(inputs);
        fixture.Store.Save(new(fixture.Store.Current with { Enabled = false }, null));
        Check(router.Resolve(connection.Settings, connection.Password, 1).Settings == connection.Settings,
            "Disabling immediately prevents new workers from using stale relay readiness");
        return Task.CompletedTask;
    }
}
