using Microsoft.Extensions.Logging.Abstractions;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class StreamingFailureTests
{
    public static async Task RunAsync()
    {
        foreach (var json in new[] { "{}", "[]", "null", "{\"streams\":null}", "{\"streams\":{}}", "{\"streams\":[null,1]}" })
        {
            Check(StreamingMediaProbe.Parse(json) is null, "Unexpected metadata shape is treated as unavailable");
        }

        var fixture = Create();
        var health = new FailingHealth();
        using var relay = new MediaRelayService(fixture.Store, fixture.Catalog, new RelayProcess(), health,
            NullLogger<MediaRelayService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await relay.StartAsync(deadline.Token);
        try
        {
            await UntilAsync(() => relay.Status.State == "retrying" || relay.ExecuteTask!.IsCompleted, deadline.Token);
            Check(relay.ExecuteTask is { IsCompleted: false }, "Unexpected relay health errors do not terminate supervision");
            await UntilAsync(() => relay.Status.Running, deadline.Token);
            Check(health.Calls >= 2, "Relay supervision recovers after an unexpected exception");

            var probe = new FailingProbe(fixture.Catalog.Profiles()[0].Token);
            using var metadata = new StreamingMetadataService(fixture.Catalog, relay, probe);
            await metadata.StartAsync(deadline.Token);
            try
            {
                await UntilAsync(() => fixture.Catalog.Profiles().Skip(1).All(p => p.Media.MetadataSource == "test"), deadline.Token);
                Check(metadata.ExecuteTask is { IsCompleted: false } && probe.Failures == 1,
                    "Failed metadata probes leave the service and other camera profiles running");
                probe.Fail = false;
                metadata.Refresh();
                await UntilAsync(() => fixture.Catalog.Profiles().All(p => p.Media.MetadataSource == "test"), deadline.Token);
                Check(true, "A failed metadata profile can be probed successfully again");
            }
            finally
            {
                await metadata.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }

        var store = new WebSettingsStore(new JsonAppSettingsStore(fixture.Configuration));
        await store.SaveConfigurationAsync(fixture.Provider.StreamingConnection.Settings, fixture.Provider.StreamingConnection.Password, CancellationToken.None);
        var recording = new FailingRecording();
        using var viewer = new ViewerService(store, fixture.Configuration, recording);
        var state = new RelayCameraInputState();
        state.Publish(new(viewer.StreamingConnection, 18554, new("test", "test")));
        using var bridge = new RelayViewerBridge(state, viewer);
        await bridge.StartAsync(CancellationToken.None);
        try
        {
            using var bridgeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await UntilAsync(() => recording.Calls >= 2 || bridge.ExecuteTask!.IsCompleted, bridgeDeadline.Token);
            Check(recording.Calls == 2 && bridge.ExecuteTask is { IsCompleted: false },
                "Failed camera transitions retry the same relay change without terminating the bridge");
        }
        finally
        {
            await bridge.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            await viewer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }

    private sealed class RelayProcess : IMediaRelayProcess
    {
        public bool Running { get; private set; }
        public Task StartAsync(string executable, string configurationPath, CancellationToken token)
        {
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

    private sealed class FailingHealth : IMediaRelayHealth
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<(int Streams, int Readers)> CheckAsync(int port, StreamingAccount account, CancellationToken token) =>
            Interlocked.Increment(ref _calls) == 1 ? throw new FormatException("Malformed relay health") : Task.FromResult((1, 0));
    }

    private sealed class FailingProbe(string failingToken) : IStreamingMediaProbe
    {
        public volatile bool Fail = true;
        public int Failures { get; private set; }
        public Task<StreamingMediaInfo?> ProbeAsync(StreamingProfile profile, CancellationToken token)
        {
            if (profile.Token == failingToken && Fail)
            {
                Failures++;
                throw new InvalidDataException("Malformed camera metadata");
            }
            return Task.FromResult<StreamingMediaInfo?>(new("H264", 640, 360, 10, 1024, MetadataSource: "test"));
        }
    }

    private sealed class FailingRecording : IRecordingConnection
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task ChangeConnectionAsync(ViewerSettings connection, string password) =>
            Interlocked.Increment(ref _calls) == 1 ? throw new InvalidDataException("Malformed camera output") : Task.CompletedTask;
    }
}
