using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

static class CameraPlaybackTests
{
    public static async Task RunAsync()
    {
        var source = new CameraSource { Channel = 1, MainStream = "rtsp://camera/live" };
        Check(source.PlaybackMethod == "automatic", "Existing configurations default to automatic camera playback");
        foreach (var method in new[] { "automatic", "native", "browser", "server" })
        {
            var camera = source with
            {
                PlaybackMethod = method
            };
            Check(camera.Validate() is null, $"Playback method {method} validates");
            Check(JsonSerializer.Deserialize<CameraSource>(JsonSerializer.Serialize(camera))?.PlaybackMethod == method,
                $"Playback method {method} round trips with the camera configuration");
        }
        foreach (var invalid in new string?[] { null, "", "NATIVE", "invalid" })
        {
            Check((source with { PlaybackMethod = invalid! }).Validate() is not null, "Invalid playback methods cannot be persisted");
        }
        Check(PreviewChannelSelection.Parse(null) is null, "Legacy preview subscribers still request all cameras");
        Check(PreviewChannelSelection.Parse("")!.Count == 0, "Empty preview subscriptions request no JPEG decoding");
        Check(PreviewChannelSelection.Parse("2,4,2")!.SetEquals([2, 4]), "Preview subscriptions select cameras and deduplicate channels");
        foreach (var invalid in new[] { "0", "-1", "one", "2,", "2147483648" })
        {
            try
            {
                PreviewChannelSelection.Parse(invalid);
            }
            catch (ArgumentException)
            {
                continue;
            }
            throw new Exception("Invalid preview channels were accepted: " + invalid);
        }
        var process = new CopyProcess();
        var sourceSettings = new ViewerSettings { Sources = [source with { MainStream = "rtsp://camera/main", SubStream = "rtsp://camera/sub" }], Substream = true };
        await new CameraVideoSource(process).RunAsync(sourceSettings, "secret", 1, _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
        Check(process.Arguments.Contains("copy") && !process.Arguments.Contains("libx264") && !process.Arguments.Contains("-vf") && process.Arguments.Contains("-an"),
            "Grid source copies encoded video and performs neither video nor audio conversion");
        Check(process.Input!.Contains("/sub") && !process.Input.Contains("/main") && !string.Join(' ', process.Arguments).Contains("secret"),
            "Grid playback honors the selected substream and keeps camera credentials out of arguments");
        Check(process.Pipeline == "grid.copy", "Grid copy CPU is identified separately for benchmarks");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await new CameraVideoSource(process).RunAsync(sourceSettings, "", 1, _ => Task.CompletedTask, _ => Task.CompletedTask, cancelled.Token);
            throw new Exception("Cancelled grid capture was started.");
        }
        catch (OperationCanceledException)
        {
            Check(process.Calls == 1, "Cancelled grid capture does not launch a process");
        }
        foreach (var initialization in new[] { LivePerformanceTests.Initialization(110), Hevc("hev1") })
        {
            var runner = new CopyProcess(initialization);
            LiveMediaInitialization? description = null;
            await new CameraVideoSource(runner).RunAsync(sourceSettings, "", 1,
                value => { description = value; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
            Check(description is not null && runner.Arguments.Contains("copy") && runner.Calls == 1,
                "Grid initialization preserves HEVC and high-bit-depth H.264 for client decoding");
        }
        var incompatible = LivePerformanceTests.Initialization();
        "jpeg"u8.CopyTo(incompatible.AsSpan(incompatible.AsSpan().IndexOf("avc1"u8)));
        try
        {
            await new CameraVideoSource(new CopyProcess(incompatible)).RunAsync(sourceSettings, "", 1,
                _ => throw new Exception("Unsupported initialization must not be published."), _ => Task.CompletedTask, CancellationToken.None);
            throw new Exception("An unsupported camera codec was accepted.");
        }
        catch (UnsupportedCameraVideoException)
        {
            Check(true, "Unsupported encoded codecs trigger explicit JPEG compatibility without server video transcoding");
        }
        await CheckAcknowledgementsAsync();
        await CheckQueueRecoveryMetadataAsync();
        foreach (var tag in new[] { "hvc1", "hev1" })
        {
            var metadata = Hevc(tag);
            Check(FfmpegLiveMediaSource.Describe(metadata).MimeType == $"video/mp4; codecs=\"{tag}.1.6.L93.B0\"",
                "HEVC MIME reflects the actual sample entry, compatibility flags, tier, level and constraints");
            var tier = Hevc(tag, 0x61);
            Check(FfmpegLiveMediaSource.Describe(tier).MimeType.Contains($"{tag}.A1.6.H93.B0"), "HEVC profile spaces and high tiers are preserved");
        }
    }

    private static async Task CheckQueueRecoveryMetadataAsync()
    {
        foreach (var packetLimited in new[] { false, true })
        {
            var options = new LiveMediaOptions
            {
                MaximumQueuedBytes = packetLimited ? 100 : 3,
                MaximumQueuedPackets = packetLimited ? 2 : 10
            };
            var releases = 0;
            await using var listener = new LiveMediaSubscription(() =>
            {
                releases++;
                return Task.CompletedTask;
            }, options);
            listener.Publish(new("init", [1]));
            listener.Publish(new("fragment", [2, 3]));
            Check(listener.QueueDiagnostics is { QueuedPackets: 2, QueuedBytes: 3 },
                "Queue diagnostics count initialization and compressed media within the configured budgets");
            listener.Publish(new("fragment", [4]));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var packets = listener.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
            Check(await packets.MoveNextAsync(), "Overflow preserves a terminal recovery message");
            var packet = packets.Current;
            Check(packet is { Status: "retrying", Reason: "queue-overflow", Bytes: null } &&
                packet.Queue is { QueuedPackets: 2, QueuedBytes: 3, IncomingBytes: 1 },
                "Both packet and byte overflow discard the stale backlog and retain pre-discard diagnostics");
            var metadata = JsonSerializer.SerializeToElement(CameraVideoEndpoints.Metadata(packet, "HTTP/2"));
            Check(!metadata.GetProperty("fallback").GetBoolean() &&
                metadata.GetProperty("reason").GetString() == "queue-overflow" &&
                metadata.GetProperty("serverProtocol").GetString() == "HTTP/2" &&
                metadata.GetProperty("queue").GetProperty("queuedBytes").GetInt32() == 3 &&
                metadata.GetProperty("queue").GetProperty("maximumQueuedPackets").GetInt32() == options.MaximumQueuedPackets,
                "The wire protocol classifies overflow as recoverable and exports only queue/protocol metadata");
            listener.Publish(new("fragment", [5]));
            Check(!await packets.MoveNextAsync(), "Closed queues cannot accept new compressed dependencies");
            await listener.DisposeAsync();
            await listener.DisposeAsync();
            Check(releases == 1, "Recovery releases the old listener only once");
        }

        foreach (var status in new[] { "connecting", "waiting", "retrying", "unsupported" })
        {
            var metadata = JsonSerializer.SerializeToElement(CameraVideoEndpoints.Metadata(
                new("state", Status: status), "HTTP/1.1"));
            Check(metadata.GetProperty("fallback").GetBoolean() == (status == "unsupported") &&
                metadata.GetProperty("queue").ValueKind == JsonValueKind.Null,
                "Only unsupported media requests codec fallback; ordinary source states remain recoverable");
        }
        var media = JsonSerializer.SerializeToElement(CameraVideoEndpoints.Metadata(
            new("fragment", "private-media-bytes"u8.ToArray()), "HTTP/1.1"));
        Check(!media.GetRawText().Contains("private-media-bytes", StringComparison.Ordinal) &&
            !media.TryGetProperty("bytes", out _), "Playback diagnostics never include compressed camera footage");
    }

    private static async Task CheckAcknowledgementsAsync()
    {
        foreach (var reply in new[] { "ok", "bad", "o", "" })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var acknowledgements = new SemaphoreSlim(0, 1);
            using var socket = new TestSocket();
            socket.Replies.Writer.TryWrite((reply, WebSocketMessageType.Text, true));
            socket.Replies.Writer.TryWrite(("", WebSocketMessageType.Close, true));
            await CameraVideoEndpoints.ReceiveAsync(socket, acknowledgements, lifetime, () => true);
            Check(lifetime.IsCancellationRequested && acknowledgements.CurrentCount == (reply == "ok" ? 1 : 0),
                "Only complete ok messages acknowledge camera fragments; unexpected data closes playback");
        }
        foreach (var type in new[] { WebSocketMessageType.Binary, WebSocketMessageType.Text })
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var acknowledgements = new SemaphoreSlim(0, 1);
            using var socket = new TestSocket();
            socket.Replies.Writer.TryWrite(("ok", type, false));
            await CameraVideoEndpoints.ReceiveAsync(socket, acknowledgements, lifetime, () => true);
            Check(lifetime.IsCancellationRequested && acknowledgements.CurrentCount == 0, "Binary or partial camera acknowledgements are rejected");
        }
        using (var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var acknowledgements = new SemaphoreSlim(0, 1))
        using (var socket = new TestSocket())
        {
            await CameraVideoEndpoints.ReceiveAsync(socket, acknowledgements, lifetime, () => false);
            Check(lifetime.IsCancellationRequested && acknowledgements.CurrentCount == 0, "Revoked read access cancels an idle camera socket");
        }
        using (var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var acknowledgements = new SemaphoreSlim(0, 1))
        using (var socket = new TestSocket())
        {
            socket.Replies.Writer.TryWrite(("ok", WebSocketMessageType.Text, true));
            socket.Replies.Writer.TryWrite(("ok", WebSocketMessageType.Text, true));
            await CameraVideoEndpoints.ReceiveAsync(socket, acknowledgements, lifetime, () => true);
            Check(lifetime.IsCancellationRequested && acknowledgements.CurrentCount == 1, "Duplicate acknowledgements cannot accumulate unbounded send credits");
        }
        var unsupported = new UnsupportedSource();
        var hub = new LiveMediaHub(unsupported);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var listener = hub.Subscribe(new(), "", 1, null))
        {
            await using var reader = listener.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
            while (await reader.MoveNextAsync() && reader.Current.Status != "unsupported")
            {
            }
            Check(reader.Current.Status == "unsupported" && unsupported.Calls == 1, "Unsupported encoded video reports JPEG compatibility without repeatedly launching decoders");
        }
        Check(hub.SessionCount == 0, "Removing the final unsupported viewer releases its waiting session");
    }

    private sealed class UnsupportedSource : ILiveMediaSource
    {
        public int Calls
        {
            get; private set;
        }
        public Task RunAsync(ViewerSettings settings, string password, int channel,
            Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            Calls++;
            throw new UnsupportedCameraVideoException();
        }
    }

    private sealed class TestSocket : WebSocket
    {
        public Channel<(string Text, WebSocketMessageType Type, bool End)> Replies { get; } = Channel.CreateUnbounded<(string, WebSocketMessageType, bool)>();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort()
        {
        }
        public override void Dispose()
        {
        }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var reply = await Replies.Reader.ReadAsync(token);
            var bytes = System.Text.Encoding.ASCII.GetBytes(reply.Text);
            bytes.AsSpan().CopyTo(buffer.AsSpan());
            return new(bytes.Length, reply.Type, reply.End);
        }
    }

    internal static byte[] Hevc(string tag, byte profile = 1, bool audio = false)
    {
        var avc = LivePerformanceTests.Initialization(audio: audio);
        var config = new byte[23];
        config[0] = 1;
        config[1] = profile;
        config[2] = 0x60;
        config[6] = 0xb0;
        config[12] = 93;
        config[21] = 3;
        // Replace the AVC configuration and adjust the sizes of its owning boxes.
        var offset = avc.AsSpan().IndexOf("avcC"u8) - 4;
        var oldSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(avc.AsSpan(offset));
        var result = avc[..offset].Concat(new byte[8]).Concat(config).Concat(avc[(offset + oldSize)..]).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset), 31);
        "hvcC"u8.CopyTo(result.AsSpan(offset + 4));
        var sample = result.AsSpan().IndexOf("avc1"u8);
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(result, sample);
        foreach (var type in new[] { "moov", "trak", "mdia", "minf", "stbl", "stsd", tag })
        {
            var at = result.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(type)) - 4;
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(at));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), (uint)(size + 31 - oldSize));
        }
        return result;
    }

    private sealed class CopyProcess(byte[]? media = null) : IFfmpegProcess
    {
        public string[] Arguments { get; private set; } = [];
        public string? Input
        {
            get; private set;
        }
        public string? Pipeline
        {
            get; private set;
        }
        public int Calls
        {
            get; private set;
        }
        public Task RunAsync(IEnumerable<string> arguments, string? input, Func<Stream, CancellationToken, Task> read,
            CancellationToken token, string pipeline = "other", int channel = 0)
        {
            Calls++;
            Arguments = arguments.ToArray();
            Input = input;
            Pipeline = pipeline;
            return media is null ? Task.CompletedTask : ReadAsync();

            async Task ReadAsync()
            {
                using var stream = new MemoryStream(media);
                await read(stream, token);
            }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }
}
