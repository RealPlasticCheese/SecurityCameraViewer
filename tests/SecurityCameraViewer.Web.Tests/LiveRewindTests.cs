using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class LiveRewindTests
{
    public static async Task RunAsync()
    {
        Check(new LiveRewindSettings(true, 86400, 8192).Validate() is null && new LiveRewindSettings(true, 1, 1).Validate() is null,
            "Rewind duration and memory budget have no arbitrary product ceilings");
        foreach (var invalid in new[] { new LiveRewindSettings(true, 0), new(true, -1), new(true, 1, 0), new(true, 1, long.MaxValue) })
        {
            Check(invalid.Validate() is not null, "Invalid or overflowing resource settings are rejected");
            Reject(() => AppSettingsValidation.Validate(new AppSettings { LiveRewind = invalid }));
        }

        var time = new TestTime();
        var initializationBudget = new LiveRewindBuffer(TimeSpan.FromSeconds(10), 100, time);
        initializationBudget.Initialize(new(new byte[40], "video/mp4", false));
        initializationBudget.Add(new(new byte[50], 1) { ProducedAt = time.GetUtcNow() });
        initializationBudget.Initialize(new(new byte[30], "video/mp4", false));
        Check(initializationBudget.Snapshot(1, "connecting", "").Bytes == 30,
            "Reconnect initialization immediately respects the byte budget before another fragment arrives");
        Reject(() => initializationBudget.Initialize(new(new byte[101], "video/mp4", false)));

        var buffer = new LiveRewindBuffer(TimeSpan.FromSeconds(10), 100, time);
        Reject(() => buffer.Add(new([1], 1)));
        buffer.Initialize(new([0], "video/mp4", true));
        for (var index = 0; index < 6; index++)
        {
            buffer.Add(new(new byte[20], 1, index) { ProducedAt = time.GetUtcNow() });
            time.Advance(1);
        }

        var snapshot = buffer.Snapshot(1, "live", "");
        var range = snapshot.Ranges.Single();
        Check(snapshot.Bytes == 81 && range.End - range.Start == 4000, "Configured byte capacity evicts only the oldest whole keyframes, including initialization accounting");
        var playlist = buffer.Playlist(range.Epoch, range.Start + 900)!;
        Check(playlist.HasAudio && playlist.Fragments[0].Start == range.Start && playlist.Fragments[0].MediaStartSeconds == 2,
            "Historical seek selects the preceding keyframe and preserves its original video clock");
        Check(buffer.Playlist(range.Epoch, range.Start - 1) is null && buffer.Playlist(range.Epoch, range.End) is null &&
            buffer.Playlist("unknown", range.Start) is null && buffer.Media(range.Epoch, 1) is null && buffer.Media(range.Epoch, null)!.SequenceEqual(new byte[] { 0 }),
            "Expired, unknown and out-of-range media are never substituted with a current frame");
        Check(buffer.Media(range.Epoch, playlist.Fragments[0].Sequence)!.Length == 20, "Retained fragments are served by sequence and epoch");
        Reject(() => buffer.Add(new(new byte[101], 1, 6)));
        Reject(() => buffer.Add(new([1], double.NaN, 6)));
        Reject(() => buffer.Add(new([1], 0, 6)));
        Reject(() => buffer.Add(new([1], 31, 6)));
        Reject(() => buffer.Add(new([1], 1, double.PositiveInfinity)));
        Reject(() => buffer.Add(new([1], 1, -1)));
        Reject(() => buffer.Add(new([1], 1, 1)));

        time.Advance(5);
        buffer.Initialize(new([2], "video/mp4", false));
        buffer.Add(new([3], 1, 0) { ProducedAt = time.GetUtcNow() });
        var epochs = buffer.Snapshot(1, "live", "").Ranges;
        Check(epochs.Length == 2 && epochs[0].Epoch != epochs[1].Epoch && epochs[1].Start > epochs[0].End,
            "Reconnect epochs preserve earlier footage and expose a real coverage gap");
        Check(buffer.Playlist(epochs[0].Epoch, epochs[0].Start)!.HasAudio && !buffer.Playlist(epochs[1].Epoch, epochs[1].Start)!.HasAudio &&
            buffer.Media(epochs[0].Epoch, null)!.SequenceEqual(new byte[] { 0 }) && buffer.Media(epochs[1].Epoch, null)!.SequenceEqual(new byte[] { 2 }),
            "Each stream epoch retains its own codec initialization and audio metadata");
        time.Advance(11);
        Check(buffer.Snapshot(1, "waiting", "").Ranges.Length == 0 && buffer.Media(epochs[0].Epoch, null) is null,
            "History expires by wall clock while a camera sleeps, without requiring new fragments");

        var longBuffer = new LiveRewindBuffer(TimeSpan.FromHours(24), 1024 * 1024, time);
        longBuffer.Initialize(new([0], "video/mp4", false));
        for (var index = 0; index < 30; index++)
        {
            longBuffer.Add(new([1], 1, index) { ProducedAt = time.GetUtcNow() });
            time.Advance(1);
        }
        var longRange = longBuffer.Snapshot(1, "live", "").Ranges.Single();
        Check(longBuffer.Playlist(longRange.Epoch, longRange.Start)!.Fragments.Length == 12,
            "Playlist delivery uses small request windows without limiting retained history");

        await LifecycleAsync();
        Console.WriteLine("All live rewind component checks passed.");
    }

    private static async Task LifecycleAsync()
    {
        var source = new TestSource();
        var hub = new LiveMediaHub(source);
        var settings = new ViewerSettings { ChannelCount = 1 };
        await hub.ConfigureRewindAsync(settings, "", new(true), _ => null);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(hub.RewindSnapshot().Single().Ranges.Length == 1 && source.Starts == 1,
            "Enabled rewind captures before a browser subscribes");
        await using var listener = hub.Subscribe(settings, "", 1, null);
        await listener.DisposeAsync();
        Check(source.Stops == 0 && hub.SessionCount == 1, "Rewind and live sound share an encoder that survives the last browser leaving");
        await using var active = hub.Subscribe(settings, "", 1, null);
        await hub.ConfigureRewindAsync(settings, "", new(false), _ => null);
        Check(hub.RewindBuffer(1) is null && source.Stops == 0, "Disabling history frees the cache while retaining an active sound listener");
        await active.DisposeAsync();
        Check(source.Stops == 1 && hub.SessionCount == 0, "Disabling rewind and closing the last listener stops capture");
        await hub.ConfigureRewindAsync(settings, "", new(true), _ => null);
        await UntilAsync(() => hub.RewindSnapshot().Single().Ranges.Length == 1);
        await hub.StopAsync();
        Check(source.Stops == 2 && hub.RewindSnapshot().Length == 0, "Disconnect removes all retained history and awaits every encoder");

        var asleep = new TestSource();
        var sleepingHub = new LiveMediaHub(asleep);
        var availability = new BatteryCameraAvailability(_ => Task.FromResult(new ProbeResult(false, 404, "Sleeping")), TimeSpan.FromMilliseconds(10));
        await sleepingHub.ConfigureRewindAsync(settings, "", new(true), _ => availability);
        await Task.Delay(80);
        Check(asleep.Starts == 0 && sleepingHub.RewindSnapshot().Single().Ranges.Length == 0,
            "Enabled rewind waits for sleeping cameras without launching an encoder or inventing footage");
        await sleepingHub.StopAsync();
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return;
        }
        throw new Exception("Expected invalid rewind input to be rejected.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class TestSource : ILiveMediaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public int Stops;

        public async Task RunAsync(ViewerSettings settings, string password, int channel, Func<LiveMediaInitialization, Task> initialized,
            Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                await initialized(new([0], "video/mp4", false));
                await fragment(new([1], 1));
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                Interlocked.Increment(ref Stops);
            }
        }
    }
}
