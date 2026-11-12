using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class DetectionSynchronizationTests
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new TimingSource();
        var hub = new LiveMediaHub(source);
        await using var listener = hub.Subscribe(new ViewerSettings(), "", 1, null);
        await using var reader = listener.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        var initialization = await NextAsync(reader, "init");
        var producedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        var fragment = new VideoFragment([1, 2, 3], 1, 5)
        {
            ProducedAt = producedAt,
            PresentationEndSeconds = 6.2
        };
        await source.PublishAsync(fragment);
        var packet = await NextAsync(reader, "fragment");
        Check(packet.StreamId == initialization.StreamId && packet.StreamId is not null &&
            packet.StartSeconds == 5 && packet.EndSeconds == 6.2 && packet.ProducedAt == producedAt,
            "Live packets preserve presentation bounds, source production time and stream identity");
        await using (var late = hub.Subscribe(new ViewerSettings(), "", 1, null))
        await using (var lateReader = late.ReadAllAsync(deadline.Token).GetAsyncEnumerator())
        {
            var replay = await NextAsync(lateReader, "fragment");
            Check(replay.ProducedAt == producedAt && replay.StreamId == packet.StreamId,
                "Joining another viewer does not retimestamp cached media or start another decoder");
        }
        await source.InitializeAsync();
        var restarted = await NextAsync(reader, "init");
        Check(restarted.StreamId != initialization.StreamId, "A new media initialization starts a distinct synchronization epoch");
        await listener.DisposeAsync();
        await hub.StopAsync();
    }

    private static async Task<LiveMediaPacket> NextAsync(IAsyncEnumerator<LiveMediaPacket> reader, string type)
    {
        while (await reader.MoveNextAsync())
        {
            if (reader.Current.Type == type)
            {
                return reader.Current;
            }
        }
        throw new Exception("Missing media packet: " + type);
    }

    private sealed class TimingSource : ILiveMediaSource
    {
        private Func<LiveMediaInitialization, Task>? _initialized;
        private Func<VideoFragment, Task>? _fragment;

        public async Task RunAsync(ViewerSettings settings, string password, int channel,
            Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            _initialized = initialized;
            _fragment = fragment;
            await InitializeAsync();
            await Task.Delay(Timeout.Infinite, token);
        }

        public Task InitializeAsync() => _initialized!(new([0], "video/mp4", false));
        public Task PublishAsync(VideoFragment fragment) => _fragment!(fragment);
    }

    private static void Check(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }
}
