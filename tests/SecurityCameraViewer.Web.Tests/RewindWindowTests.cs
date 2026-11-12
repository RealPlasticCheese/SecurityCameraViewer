using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class RewindWindowTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        var time = new TestTime();
        var buffer = new LiveRewindBuffer(TimeSpan.FromSeconds(3), 100, time);
        buffer.Initialize(new([1, 2], "video/mp4", true));
        for (var index = 0; index < 3; index++)
        {
            buffer.Add(Fragment(index, index + 1));
        }
        var range = buffer.Snapshot(1, "live", "").Ranges.Single();
        var admitted = buffer.Window(range.Epoch, range.Start)!;
        Check(admitted.Playlist.Fragments.Length == 3 && admitted.Fragments.Length == 3,
            "Oldest-edge load admits matching timing, initialization and complete keyframe media together");
        time.Now = Origin.AddSeconds(10);
        Check(buffer.Window(range.Epoch, range.Start) is null && buffer.Media(range.Epoch, 1) is null,
            "New reads correctly reject expired media while an admitted window remains valid");
        await VerifyResponseAsync(admitted);

        var capacity = new LiveRewindBuffer(TimeSpan.FromDays(1), 4, time);
        capacity.Initialize(new([7], "video/mp4", false));
        capacity.Add(Fragment(0, 1) with { Bytes = [3, 4] });
        var firstRange = capacity.Snapshot(1, "", "").Ranges.Single();
        var first = capacity.Window(firstRange.Epoch, firstRange.Start)!;
        capacity.Add(Fragment(1, 2) with { Bytes = [5, 6] });
        capacity.Initialize(new([9], "video/mp4", false));
        capacity.Add(Fragment(0, 3) with { Bytes = [8] });
        Check(capacity.Window(firstRange.Epoch, firstRange.Start) is null && first.Initialization.SequenceEqual(new byte[] { 7 }) &&
            first.Fragments.Single().SequenceEqual(new byte[] { 3, 4 }),
            "Memory eviction and reconnect cannot replace an admitted window with a newer stream");
        await VerifyResponseAsync(first);

        var startup = new LiveRewindBuffer(TimeSpan.FromDays(1), 100, time);
        startup.Initialize(new([0], "video/mp4", false));
        startup.Add(Fragment(0, 3));
        var startupRange = startup.Snapshot(1, "", "").Ranges.Single();
        var filling = startup.Window(startupRange.Epoch, startupRange.Start)!;
        var captured = filling.Playlist.Fragments[0];
        startup.Add(Fragment(1, 3.2));
        startup.Add(Fragment(2, 3.4));
        startup.Add(Fragment(3, 4));
        Check(filling.Playlist.Fragments[0] == captured &&
            startup.Snapshot(1, "", "").Ranges.Single().Start < captured.Start,
            "Startup calibration cannot change timing halfway through an admitted browser load");
        await VerifyResponseAsync(filling);

        var dependencies = new LiveRewindBuffer(TimeSpan.FromDays(1), 100, time);
        dependencies.Initialize(new([0], "video/mp4", false));
        for (var index = 0; index < 25; index++)
        {
            dependencies.Add(Fragment(index, index + 1) with { StartsWithKeyframe = index == 0 });
        }
        var dependencyRange = dependencies.Snapshot(1, "", "").Ranges.Single();
        var window = dependencies.Window(dependencyRange.Epoch, Origin.AddMilliseconds(17500).ToUnixTimeMilliseconds())!;
        Check(window.Fragments.Length == 25 && window.Playlist.Fragments[0].MediaStartSeconds == 0,
            "An atomic window includes every dependency through a long native keyframe interval");
        Check(dependencies.Window("unknown", dependencyRange.Start) is null &&
            dependencies.Window(dependencyRange.Epoch, dependencyRange.Start - 1) is null &&
            dependencies.Window(dependencyRange.Epoch, dependencyRange.End) is null,
            "Atomic windows preserve exact historical bounds and epoch identity");

        foreach (var cancelAfterPrefix in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
            using var output = cancelAfterPrefix ? new CancelAfterWriteStream(cancellation) : new MemoryStream();
            context.Response.Body = output;
            if (!cancelAfterPrefix)
            {
                cancellation.Cancel();
            }
            try
            {
                await new RewindPlaybackWindowResult(window).ExecuteAsync(context);
                throw new Exception("An aborted playback response must stop streaming media.");
            }
            catch (OperationCanceledException)
            {
                Check(output.Length == (cancelAfterPrefix ? 4 : 0), "Cancelled playback stops at the next response write without a retained server lease");
            }
        }
        Console.WriteLine("All atomic rewind window checks passed.");
    }

    private static async Task VerifyResponseAsync(RewindPlaybackWindow window)
    {
        var context = new DefaultHttpContext();
        using var output = new MemoryStream();
        context.Response.Body = output;
        await new RewindPlaybackWindowResult(window).ExecuteAsync(context);
        var bytes = output.ToArray();
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var header = JsonSerializer.Deserialize<RewindWindowHeader>(bytes.AsSpan(4, length), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var media = window.Initialization.Concat(window.Fragments.SelectMany(fragment => fragment)).ToArray();
        Check(header.Playlist.Epoch == window.Playlist.Epoch && header.Playlist.Fragments.SequenceEqual(window.Playlist.Fragments) &&
            header.InitializationLength == window.Initialization.Length &&
            header.FragmentLengths.SequenceEqual(window.Fragments.Select(fragment => fragment.Length)) &&
            bytes.AsSpan(4 + length).SequenceEqual(media) && context.Response.ContentLength == bytes.Length &&
            context.Response.ContentType == "application/octet-stream" && context.Response.Headers.CacheControl == "no-store",
            "An admitted response preserves exact metadata and compressed bytes after expiry, eviction or refinement");
    }

    private static VideoFragment Fragment(double start, double produced) => new([(byte)start], 1, start)
    {
        ProducedAt = Origin.AddMilliseconds(Math.Round(produced * 1000))
    };

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Origin.AddSeconds(3);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CancelAfterWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var result = base.WriteAsync(buffer, token);
            cancellation.Cancel();
            return result;
        }
    }
}
