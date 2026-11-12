using System.Buffers.Binary;
using System.Threading.Channels;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class LivePerformanceTests
{
    public static async Task RunAsync()
    {
        foreach (var profile in new byte[] { 66, 77, 100 })
        {
            var runner = new TestProcess(Media(profile));
            var initialized = 0;
            var fragments = new List<VideoFragment>();
            await new FfmpegLiveMediaSource(runner).RunAsync(new(), "private-test-password", 1,
                metadata => { initialized++; return Task.CompletedTask; },
                item => { fragments.Add(item); return Task.CompletedTask; }, CancellationToken.None);
            Check(runner.Arguments.Count == 1 && Codec(runner.Arguments[0]) == "copy",
                "Interoperable H.264 profiles use one passthrough process");
            Check(!runner.Arguments[0].Contains("-vf") && !runner.Arguments[0].Contains("-force_key_frames") &&
                runner.Arguments[0].Contains("1000000") && initialized == 1 && fragments.Count == 1,
                "Passthrough preserves source video and emits bounded-duration media fragments");
            Check(!runner.Arguments[0].Any(argument => argument.Contains("private-test-password")),
                "Passthrough credentials never enter process arguments");
        }

        foreach (var tag in new[] { "hvc1", "hev1" })
        {
            foreach (var profile in new byte[] { 1, 2 })
            {
                foreach (var audio in new[] { false, true })
                {
                    var initialization = CameraPlaybackTests.Hevc(tag, profile, audio);
                    var media = Join(initialization, Media(100)[Initialization().Length..]);
                    var runner = new TestProcess(media, Media(66));
                    LiveMediaInitialization? description = null;
                    var fragments = new List<VideoFragment>();
                    await new FfmpegLiveMediaSource(runner).RunAsync(new(), "", 1,
                        value => { description = value; return Task.CompletedTask; },
                        item => { fragments.Add(item); return Task.CompletedTask; }, CancellationToken.None);
                    Check(runner.Arguments.Count == 1 && Codec(runner.Arguments[0]) == "copy" &&
                        !runner.Arguments[0].Contains("-vf") && runner.Arguments[0].Contains("aac"),
                        $"{tag} profile {profile} copies video while retaining AAC audio conversion");
                    Check(description is not null && description.Bytes.SequenceEqual(initialization) &&
                        description.H264Profile is null && description.HasAudio == audio && description.MimeType.Contains($"{tag}.{profile}.") &&
                        description.MimeType.Contains("mp4a.40.2") == audio && fragments.Count == 1,
                        "HEVC initialization, optional audio and compressed fragments are published without fallback");
                    var hevcHistory = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 1024 * 1024);
                    hevcHistory.Initialize(description!);
                    hevcHistory.Add(fragments.Single());
                    var hevcRange = hevcHistory.Snapshot(1, "live", "").Ranges.Single();
                    Check(hevcHistory.Media(hevcRange.Epoch, null)!.SequenceEqual(initialization) &&
                        hevcHistory.Media(hevcRange.Epoch, hevcHistory.Playlist(hevcRange.Epoch, hevcRange.Start)!.Fragments[0].Sequence)!
                            .SequenceEqual(fragments.Single().Bytes),
                        "Rewind retains the original HEVC initialization and fragment bytes");
                }
            }
        }

        foreach (var incompatible in new[] { Media(0), Media(110), Media(122), Media(244), Media(100, codec: "hvc1") })
        {
            var runner = new TestProcess(incompatible, Media(66));
            var initialized = 0;
            await new FfmpegLiveMediaSource(runner).RunAsync(new(), "", 1,
                _ => { initialized++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
            Check(runner.Arguments.Count == 2 && Codec(runner.Arguments[1]) == "libx264" && initialized == 1,
                "Invalid HEVC metadata and unsupported H.264 profiles fall back before publishing initialization");
        }

        var forced = new TestProcess(Media(66));
        await new FfmpegLiveMediaSource(forced, options: new()
        {
            ForceTranscoding = true
        }).RunAsync(new(), "", 1,
            _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
        Check(forced.Arguments.Count == 1 && Codec(forced.Arguments[0]) == "libx264",
            "Administrators can force the software compatibility path for a browser");

        foreach (var initialization in new[] { Initialization(), CameraPlaybackTests.Hevc("hvc1"), CameraPlaybackTests.Hevc("hev1") })
        {
            var media = Join(initialization, Media(100, payloadBytes: 2048)[Initialization().Length..]);
            var oversized = new TestProcess(media, Media(66));
            var epochs = 0;
            await new FfmpegLiveMediaSource(oversized, options: new()
            {
                MaximumQueuedBytes = 1024
            }).RunAsync(new(), "", 1,
                _ => { epochs++; return Task.CompletedTask; }, _ => Task.CompletedTask, CancellationToken.None);
            Check(oversized.Arguments.Count == 2 && Codec(oversized.Arguments[1]) == "libx264" && epochs == 2,
                "H.264/HEVC passthrough exceeding the listener budget starts a fresh transcoded epoch");
        }

        var beforeInitialization = new TestProcess(Media(66)) { FailAtCall = 1 };
        await new FfmpegLiveMediaSource(beforeInitialization).RunAsync(new(), "", 1,
            _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
        Check(beforeInitialization.Arguments.Count == 2 && Codec(beforeInitialization.Arguments[1]) == "libx264",
            "A codec that cannot be muxed into MP4 can use the software fallback");

        var afterInitialization = new TestProcess(Media(100)) { FailAfterRead = true };
        await ThrowsAsync<VideoSourceException>(() => new FfmpegLiveMediaSource(afterInitialization).RunAsync(new(), "", 1,
            _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        Check(afterInitialization.Arguments.Count == 1, "An established stream failure is not silently turned into transcoding");
        var callbackFailure = new TestProcess(Media(100));
        await ThrowsAsync<InvalidOperationException>(() => new FfmpegLiveMediaSource(callbackFailure).RunAsync(new(), "", 1,
            _ => throw new InvalidOperationException("Test callback"), _ => Task.CompletedTask, CancellationToken.None));
        Check(callbackFailure.Arguments.Count == 1, "Consumer failures propagate without starting another process");
        var sourceCallbackFailure = new TestProcess(Media(100));
        await ThrowsAsync<VideoSourceException>(() => new FfmpegLiveMediaSource(sourceCallbackFailure).RunAsync(new(), "", 1,
            _ => throw new VideoSourceException("Test consumer"), _ => Task.CompletedTask, CancellationToken.None));
        Check(sourceCallbackFailure.Arguments.Count == 1, "A consumer's video exception cannot trigger codec fallback");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = new TestProcess(Media(100));
        await ThrowsAsync<OperationCanceledException>(() => new FfmpegLiveMediaSource(stopped).RunAsync(new(), "", 1,
            _ => Task.CompletedTask, _ => Task.CompletedTask, cancelled.Token));
        Check(stopped.Arguments.Count == 0, "Cancelled capture launches neither passthrough nor fallback");
        foreach (var invalid in new[] { new LiveMediaOptions { MaximumQueuedBytes = 0 }, new() { MaximumQueuedPackets = -1 } })
        {
            await ThrowsAsync<ArgumentException>(() => new FfmpegLiveMediaSource(stopped, options: invalid).RunAsync(new(), "", 1,
                _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        }

        foreach (var (firstFlags, expected) in new[] { (0x02000000u, true), (0x01010000u, false), (0x01000000u, false), (0u, true) })
        {
            var parsed = new List<VideoFragment>();
            await FragmentedMp4.ReadAsync(new MemoryStream(Media(100, firstFlags: firstFlags)), _ => Task.CompletedTask,
                item => { parsed.Add(item); return Task.CompletedTask; }, CancellationToken.None);
            Check(parsed.Single().StartsWithKeyframe == expected, "Video sample flags distinguish independent and dependent fragments");
        }
        foreach (var mode in new[] { "trex", "tfhd", "sample", "offsets" })
        {
            foreach (var keyframe in new[] { true, false })
            {
                var parsed = new List<VideoFragment>();
                await FragmentedMp4.ReadAsync(new MemoryStream(FlagMedia(mode, keyframe ? 0x02000000u : 0x01010000u)),
                    _ => Task.CompletedTask, item => { parsed.Add(item); return Task.CompletedTask; }, CancellationToken.None);
                Check(parsed.Single().StartsWithKeyframe == keyframe, "Keyframe flags are read from " + mode + " with audio and empty runs skipped");
            }
        }
        await ThrowsAsync<InvalidDataException>(() => FragmentedMp4.ReadAsync(new MemoryStream(FlagMedia("truncated", 0)),
            _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));

        var history = new LiveRewindBuffer(TimeSpan.FromHours(1), 1024 * 1024);
        history.Initialize(new(Initialization(), "video/mp4", false));
        history.Add(new([0], 1, 0));
        history.Add(new([1], 1, 1, false));
        history.Add(new([2], 1, 2, false));
        history.Add(new([3], 1, 3));
        var range = history.Snapshot(1, "live", "").Ranges.Single();
        Check(history.Playlist(range.Epoch, range.Start + 2500)!.Fragments[0].MediaStartSeconds == 0,
            "A seek between native keyframes includes the preceding keyframe and all dependencies");
        Check(history.Playlist(range.Epoch, range.Start + 3500)!.Fragments[0].MediaStartSeconds == 3,
            "A seek after the next keyframe uses that new independent fragment");

        var capacity = new LiveRewindBuffer(TimeSpan.FromHours(1), Initialization().Length + 3);
        capacity.Initialize(new(Initialization(), "video/mp4", false));
        capacity.Add(new([0], 1, 0));
        capacity.Add(new([1], 1, 1, false));
        capacity.Add(new([2], 1, 2, false));
        capacity.Add(new([3], 1, 3));
        Check(capacity.Snapshot(1, "live", "").Ranges.Single().End - capacity.Snapshot(1, "live", "").Ranges.Single().Start == 1000,
            "Evicting a keyframe also evicts its dependent fragments");
        var noKeyframe = new LiveRewindBuffer(TimeSpan.FromHours(1), 1024 * 1024);
        noKeyframe.Initialize(new(Initialization(), "video/mp4", false));
        noKeyframe.Add(new([1], 1, 1, false));
        Check(noKeyframe.Snapshot(1, "live", "").Ranges.Length == 0,
            "An epoch without a keyframe cannot advertise playable history");
        history.Initialize(new(Initialization(), "video/mp4", false));
        history.Add(new([4], 1, 0, false));
        Check(history.Snapshot(1, "live", "").Ranges.Length == 1,
            "A new epoch's dependent first fragment cannot become playable behind an older epoch");
        history.Add(new([5], 1, 1));
        Check(history.Snapshot(1, "live", "").Ranges.Length == 2,
            "The new epoch starts advertising history at its first keyframe");
        var longGop = new LiveRewindBuffer(TimeSpan.FromHours(1), 1024 * 1024);
        longGop.Initialize(new(Initialization(), "video/mp4", false));
        var captureOrigin = DateTimeOffset.UtcNow.AddSeconds(-25);
        for (var i = 0; i < 25; i++)
        {
            longGop.Add(new([(byte)i], 1, i, i == 0)
            {
                ProducedAt = captureOrigin.AddSeconds(i + 1)
            });
        }
        var longRange = longGop.Snapshot(1, "live", "").Ranges.Single();
        Check(longGop.Playlist(longRange.Epoch, longRange.Start + 17500)!.Fragments.Length == 25,
            "Long native keyframe intervals include every dependency through the requested seek time");
        await ListenerKeyframesAsync();
        await QueueBudgetsAsync();
    }

    private static async Task QueueBudgetsAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var capacity = Initialization().Length + 6;
        var source = new ControlledSource();
        var hub = new LiveMediaHub(source, new()
        {
            MaximumQueuedBytes = capacity,
            MaximumQueuedPackets = 3
        });
        await using var listener = hub.Subscribe(new(), "", 1, null);
        await using var reader = listener.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && await reader.MoveNextAsync(), "Configured queue budgets accept initialization");
        await source.Frames.Writer.WriteAsync(new(new byte[capacity], 1), deadline.Token);
        Check(await reader.MoveNextAsync() && reader.Current.Bytes!.Length == capacity,
            "Configured byte budgets accept a fragment exactly at capacity");
        await source.Frames.Writer.WriteAsync(new(new byte[capacity + 1], 1), deadline.Token);
        Check(await reader.MoveNextAsync() && reader.Current.Status == "retrying" && !await reader.MoveNextAsync(),
            "An oversized fragment closes the queue without publishing corrupt media");
        await listener.DisposeAsync();
        await listener.DisposeAsync();
        Check(hub.SessionCount == 0, "Closed listeners release their source lease only once");

        var packetSource = new ControlledSource();
        var packetHub = new LiveMediaHub(packetSource, new()
        {
            MaximumQueuedBytes = capacity,
            MaximumQueuedPackets = 2
        });
        await using var packets = packetHub.Subscribe(new(), "", 1, null);
        await using var packetReader = packets.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Check(await packetReader.MoveNextAsync() && await packetReader.MoveNextAsync(), "Message budgets accept initial state and metadata");
        for (var i = 0; i < 3; i++)
        {
            await packetSource.Frames.Writer.WriteAsync(new([(byte)i], 1), deadline.Token);
        }
        while (Volatile.Read(ref packetSource.Delivered) < 3)
        {
            await Task.Delay(10, deadline.Token);
        }
        Check(await packetReader.MoveNextAsync() && packetReader.Current.Status == "retrying" && !await packetReader.MoveNextAsync(),
            "Configured message budgets disconnect slow listeners before queued bytes reach their limit");
        await packetHub.StopAsync();
    }

    private static async Task ListenerKeyframesAsync()
    {
        var source = new ControlledSource();
        var hub = new LiveMediaHub(source);
        await using var subscription = hub.Subscribe(new(), "", 1, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var packets = subscription.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Check(await packets.MoveNextAsync() && packets.Current.Type == "state", "New listeners receive state");
        Check(await packets.MoveNextAsync() && packets.Current.Type == "init", "New listeners receive initialization");
        var firstFrame = packets.MoveNextAsync().AsTask();
        await source.Frames.Writer.WriteAsync(new([1], 1, 1, false), deadline.Token);
        await Task.Delay(30, deadline.Token);
        Check(!firstFrame.IsCompleted, "New listeners wait for an independent fragment");
        await source.Frames.Writer.WriteAsync(new([2], 1, 2), deadline.Token);
        Check(await firstFrame && packets.Current.Bytes!.SequenceEqual(new byte[] { 2 }), "The next keyframe starts a new listener");
        await source.Frames.Writer.WriteAsync(new([3], 1, 3, false), deadline.Token);
        Check(await packets.MoveNextAsync() && packets.Current.Bytes!.SequenceEqual(new byte[] { 3 }),
            "Established listeners retain dependent fragments in order");
        Check(hub.IsLive(1) && !hub.IsLive(2), "Live media health is independent of paused JPEG previews");
        await source.Frames.Writer.WriteAsync(new([4], 1, 4), deadline.Token);
        Check(await packets.MoveNextAsync(), "An independent fragment remains available before an epoch change");
        await source.Initialize(new(Initialization(66), "video/mp4", false));
        Check(await packets.MoveNextAsync() && packets.Current.Type == "init", "Codec fallback publishes a fresh epoch to established listeners");
        await using var late = hub.Subscribe(new(), "", 1, null);
        await using var latePackets = late.ReadAllAsync(deadline.Token).GetAsyncEnumerator();
        Check(await latePackets.MoveNextAsync() && await latePackets.MoveNextAsync(), "A late listener receives the new epoch's metadata");
        var waiting = latePackets.MoveNextAsync().AsTask();
        await Task.Delay(30, deadline.Token);
        Check(!waiting.IsCompleted && !hub.IsLive(1), "A new codec epoch cannot reuse compressed footage from the previous codec");
        await source.Frames.Writer.WriteAsync(new([5], 1, 5), deadline.Token);
        Check(await waiting && latePackets.Current.Bytes!.SequenceEqual(new byte[] { 5 }), "A late listener starts with the new epoch's keyframe");
        await hub.StopAsync();
        Check(!hub.IsLive(1), "Stopped media does not retain a live camera status");
    }

    private static string Codec(string[] arguments) => arguments[Array.IndexOf(arguments, "-c:v") + 1];

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            Check(true, typeof(T).Name + " is propagated");
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    public static Task CharacterizeAsync()
    {
        foreach (var profile in new byte[] { 66, 77, 100 })
        {
            foreach (var audio in new[] { false, true })
            {
                var initialization = Initialization(profile, audio);
                var described = FfmpegLiveMediaSource.Describe(initialization);
                Check(ReferenceEquals(initialization, described.Bytes), "Live initialization retains its immutable owner");
                Check(described.H264Profile == profile && described.HasAudio == audio && described.MimeType ==
                    $"video/mp4; codecs=\"avc1.{profile:X2}0028{(audio ? ",mp4a.40.2" : "")}\"",
                    "Live metadata describes the actual H.264 profile and optional audio");
            }
        }

        Reject(() => FfmpegLiveMediaSource.Describe(Box("moov", [])), "Initialization without video is rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(Initialization(100, false, "hvc1")), "HEVC is not described as H.264");
        Reject(() => FfmpegLiveMediaSource.Describe(Box("moov", Box("trak", Box("mdia", Box("hdlr", []))))),
            "Truncated media handlers are rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(Initialization(100, false, sampleBytes: 77)),
            "Truncated AVC sample entries are rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(Initialization(100, false, sampleBytes: 69, configurationBytes: 0)),
            "An AVC entry shorter than its fixed header is rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(Initialization(100, false, configurationBytes: 3)),
            "Truncated AVC configuration is rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(new byte[8]), "Invalid MP4 box sizes are rejected");
        Reject(() => FfmpegLiveMediaSource.Describe(new byte[7]), "Truncated MP4 box headers are rejected");
        var missingDescription = Initialization();
        var descriptionOffset = missingDescription.AsSpan().IndexOf("stsd"u8) - 4;
        BinaryPrimitives.WriteUInt32BigEndian(missingDescription.AsSpan(descriptionOffset), 12);
        Reject(() => FfmpegLiveMediaSource.Describe(missingDescription), "Truncated video description tables are rejected");
        var oversized = Initialization();
        BinaryPrimitives.WriteUInt32BigEndian(oversized, (uint)oversized.Length + 1);
        Reject(() => FfmpegLiveMediaSource.Describe(oversized), "MP4 boxes cannot extend past their owner");

        var buffer = new LiveRewindBuffer(TimeSpan.FromHours(1), 1024 * 1024);
        buffer.Initialize(new(Initialization(), "video/mp4", false));
        buffer.Add(new([1], 2, 0));
        buffer.Add(new([2], 2, 2));
        var range = buffer.Snapshot(1, "live", "").Ranges.Single();
        var playlist = buffer.Playlist(range.Epoch, range.Start + 2500)!;
        Check(playlist.Fragments[0].MediaStartSeconds == 2, "Rewind seeks to the containing independent fragment");
        Check(buffer.Media(range.Epoch, playlist.Fragments[0].Sequence)!.SequenceEqual(new byte[] { 2 }),
            "Rewind returns the exact selected compressed fragment");
        return Task.CompletedTask;
    }

    internal static byte[] Initialization(byte profile = 100, bool audio = false, string codec = "avc1",
        int sampleBytes = 78, int configurationBytes = 4)
    {
        var configuration = new byte[configurationBytes];
        new byte[] { 1, profile, 0, 40 }.AsSpan(0, configurationBytes).CopyTo(configuration);
        var description = Box("stsd", Join(new byte[8], Box(codec, Join(new byte[sampleBytes], Box("avcC", configuration)))));
        var trackHeader = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(trackHeader.AsSpan(12), 1);
        var mediaHeader = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(mediaHeader.AsSpan(12), 1000);
        var defaults = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(defaults.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(defaults.AsSpan(12), 1000);
        var track = Box("trak", Join(Box("tkhd", trackHeader), Box("mdia",
            Join(Box("mdhd", mediaHeader), Handler("vide"), Box("minf", Box("stbl", description))))));
        return Box("moov", Join(track, Box("mvex", Box("trex", defaults)),
            audio ? Box("trak", Box("mdia", Handler("soun"))) : []));
    }

    private static byte[] Media(byte profile, string codec = "avc1", int payloadBytes = 1, uint firstFlags = 0x02000000)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        var run = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(run, 4);
        BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(8), firstFlags);
        return Join(Initialization(profile, codec: codec), Box("moof", Box("traf",
            Join(Box("tfhd", header), Box("tfdt", new byte[8]), Box("trun", run)))), Box("mdat", new byte[payloadBytes]));
    }

    private static byte[] FlagMedia(string mode, uint flags)
    {
        var initialization = Initialization();
        var defaultsOffset = initialization.AsSpan().IndexOf("trex"u8);
        BinaryPrimitives.WriteUInt32BigEndian(initialization.AsSpan(defaultsOffset + 24), flags);
        var header = mode == "offsets" ? new byte[32] : mode is "tfhd" or "truncated" ? new byte[12] : new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, mode == "offsets" ? 0x3bu : mode is "tfhd" or "truncated" ? 0x20u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        if (mode == "offsets")
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28), flags);
        }
        else if (mode is "tfhd" or "truncated")
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), flags);
            if (mode == "truncated")
            {
                header = header[..8];
            }
        }
        var run = mode == "sample" ? new byte[24] : mode == "offsets" ? new byte[16] : new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(run, mode == "sample" ? 0xf00u : mode == "offsets" ? 5u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(4), 1);
        if (mode == "sample")
        {
            BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(8), 1000);
            BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(12), 1);
            BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(16), flags);
        }
        else if (mode == "offsets")
        {
            BinaryPrimitives.WriteUInt32BigEndian(run.AsSpan(12), flags);
        }
        var audioHeader = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(audioHeader.AsSpan(4), 2);
        return Join(initialization, Box("moof", Join(Box("free", []), Box("traf", Box("tfhd", audioHeader)),
            Box("traf", Join(Box("tfhd", header), Box("tfdt", new byte[8]), Box("trun", new byte[8]),
                Box("free", []), Box("trun", run))))), Box("mdat", [0]));
    }

    private sealed class TestProcess(params byte[][] outputs) : IFfmpegProcess
    {
        public List<string[]> Arguments { get; } = [];
        public int FailAtCall
        {
            get; init;
        }
        public bool FailAfterRead
        {
            get; init;
        }
        private int _output;

        public async Task RunAsync(IEnumerable<string> arguments, string? input, Func<Stream, CancellationToken, Task> read,
            CancellationToken token, string pipeline = "other", int channel = 0)
        {
            Arguments.Add(arguments.ToArray());
            if (Arguments.Count == FailAtCall)
            {
                throw new VideoSourceException("Test muxer failure");
            }
            await read(new MemoryStream(outputs[_output++]), token);
            if (FailAfterRead)
            {
                throw new VideoSourceException("Test established stream failure");
            }
        }
    }

    private sealed class ControlledSource : ILiveMediaSource
    {
        public Channel<VideoFragment> Frames { get; } = Channel.CreateUnbounded<VideoFragment>();
        public int Delivered;
        public Func<LiveMediaInitialization, Task> Initialize { get; private set; } = null!;

        public async Task RunAsync(SecurityCameraViewer.Core.ViewerSettings settings, string password, int channel,
            Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            Initialize = initialized;
            await initialized(new(Initialization(), "video/mp4", false));
            await foreach (var item in Frames.Reader.ReadAllAsync(token))
            {
                await fragment(item);
                Interlocked.Increment(ref Delivered);
            }
        }
    }

    private static byte[] Handler(string type)
    {
        var payload = new byte[12];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(payload, 8);
        return Box("hdlr", payload);
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static byte[] Join(params byte[][] values) => values.SelectMany(value => value).ToArray();

    private static void Reject(Action action, string name)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            Check(true, name);
            return;
        }
        throw new Exception("FAIL: " + name);
    }

    internal static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }
}
