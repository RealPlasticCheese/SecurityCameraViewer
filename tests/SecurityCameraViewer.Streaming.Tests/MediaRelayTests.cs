using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class MediaRelayTests
{
    public static async Task RunAsync()
    {
        var fixture = Create();
        var configuration = MediaMtxConfiguration.Create(fixture.Store.Current, fixture.Store.Account, fixture.Catalog, new("scv-test", "internal-secret"));
        using var document = JsonDocument.Parse(configuration);
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().ToArray();
        Check(paths.Length == 3 && paths.All(p => p.Value.GetProperty("sourceOnDemand").GetBoolean()),
            "Relay pulls one feed per requested path and excludes disabled cameras");
        Check(paths.Any(p => p.Value.GetProperty("source").GetString()!.Contains("individual-user:individual-secret")) &&
            paths.Any(p => p.Value.GetProperty("source").GetString()!.Contains("camera-user:upstream-password")),
            "Relay resolves per-camera and shared credentials independently");
        Check(document.RootElement.GetProperty("rtspAuthMethods").GetArrayLength() == 2 &&
            document.RootElement.GetProperty("authInternalUsers")[0].GetProperty("permissions")[0].GetProperty("action").GetString() == "read",
            "External RTSP accounts have read-only access with Basic/Digest compatibility");
        Check(document.RootElement.GetProperty("apiAddress").GetString()!.StartsWith("127.0.0.1:") &&
            document.RootElement.GetProperty("authInternalUsers")[1].GetProperty("ips").GetArrayLength() == 2,
            "Relay administration is authenticated and restricted to loopback");
        Check(new[] { "rtmp", "hls", "webrtc", "srt", "moq", "playback", "metrics", "pprof" }.All(p => !document.RootElement.GetProperty(p).GetBoolean()),
            "Unneeded MediaMTX listeners are disabled");
        var special = fixture.Provider.StreamingConnection;
        fixture.Provider.StreamingConnection = special with { Password = "p@ss:/?#% 'é" };
        using var escaped = JsonDocument.Parse(MediaMtxConfiguration.Create(fixture.Store.Current, fixture.Store.Account, fixture.Catalog, new("scv-test", "secret")));
        var source = new Uri(escaped.RootElement.GetProperty("paths").EnumerateObject().First().Value.GetProperty("source").GetString()!);
        Check(Uri.UnescapeDataString(source.UserInfo.Split(':', 2)[1]) == "p@ss:/?#% 'é", "Upstream passwords with reserved URL characters are encoded without changing them");
        fixture.Provider.StreamingConnection = special;

        var process = new ProcessFake();
        var health = new HealthFake();
        using var relay = new MediaRelayService(fixture.Store, fixture.Catalog, process, health, NullLogger<MediaRelayService>.Instance);
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 1 && relay.Status.Running && File.Exists(fixture.Store.RuntimePath),
            "Enabling starts the relay with a private runtime configuration and verifies readiness");
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 1, "Unchanged settings do not restart existing RTSP clients");
        fixture.Store.Save(new(fixture.Store.Current, "new-client-secret"));
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 2, "Rotating client credentials restarts the relay to invalidate existing sessions");
        fixture.Provider.StreamingConnection = special with { Settings = special.Settings with { Sources = [special.Settings.Sources[0]] } };
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 3, "Camera configuration changes reconcile relay paths automatically");
        process.Crash();
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 4 && relay.Status.Running, "A crashed relay is restarted without stopping the web server");
        health.Fail = true;
        for (var index = 0; index < 5; index++)
        {
            await relay.ReconcileAsync(CancellationToken.None);
        }

        Check(!relay.Status.Running && relay.Status.State == "retrying" && !File.Exists(fixture.Store.RuntimePath),
            "Persistent health failures stop the relay, erase runtime credentials and back off");
        await relay.ReconcileAsync(CancellationToken.None);
        Check(process.Starts == 4, "Failure backoff prevents a rapid restart loop");
        fixture.Store.Save(new(fixture.Store.Current with { Enabled = false }, null));
        await relay.ReconcileAsync(CancellationToken.None);
        Check(relay.Status.State == "disabled" && !process.Running && !File.Exists(fixture.Store.RuntimePath),
            "Disabling stops the child and removes private runtime configuration");
    }

    private sealed class ProcessFake : IMediaRelayProcess
    {
        public bool Running { get; private set; }
        public int Starts { get; private set; }
        public void Crash() => Running = false;

        public Task StartAsync(string executable, string configurationPath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Starts++;
            Running = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Running = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HealthFake : IMediaRelayHealth
    {
        public bool Fail { get; set; }
        public Task<(int Streams, int Readers)> CheckAsync(int port, StreamingAccount account, CancellationToken token) =>
            Fail ? throw new HttpRequestException("Fixture health failure") : Task.FromResult((1, 2));
    }
}
