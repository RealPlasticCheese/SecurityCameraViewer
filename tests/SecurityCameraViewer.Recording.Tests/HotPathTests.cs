using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class HotPathTests
{
    public static async Task RunAsync()
    {
        Regions();
        Images();
        Resizing();
        await FragmentsAsync();
    }

    private static void Resizing()
    {
        var type = typeof(TemplateMotionDetector).GetNestedType("Worker", BindingFlags.NonPublic)!;
        var worker = Activator.CreateInstance(type, new DetectionPlanGroup(new CameraRecordingSettings { AnalysisWidth = 16 }, null), null)!;
        var resize = type.GetMethod("Resize")!;
        foreach (var (width, height, rgb) in new[] { (16, 9, false), (32, 18, false), (47, 23, true), (80, 45, true), (47, 23, false), (47, 23, true), (16, 9, true) })
        {
            var channels = rgb ? 3 : 1;
            var pixels = Enumerable.Range(0, width * height * channels).Select(index => (byte)(index * 37)).ToArray();
            var input = new AnalysisFrame(pixels, width, height, rgb, DateTimeOffset.UnixEpoch);
            var output = (AnalysisFrame)resize.Invoke(worker, [input])!;
            if (width <= 16 && height <= 9)
            {
                Check(ReferenceEquals(output.Pixels, pixels), "Template resizing bypasses frames within the requested dimensions", false);
                continue;
            }
            var expected = new byte[16 * 9 * channels];
            for (var y = 0; y < 9; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    for (var channel = 0; channel < channels; channel++)
                    {
                        expected[(y * 16 + x) * channels + channel] = pixels[(y * height / 9 * width + x * width / 16) * channels + channel];
                    }
                }
            }
            Check(output.Pixels.SequenceEqual(expected) && output.CapturedAt == input.CapturedAt && output.IsRgb == rgb && output.Width == 16 && output.Height == 9,
                "Nearest-neighbor resize preserves pixel format, geometry and time across source shape changes", false);
        }
        var aiWorker = Activator.CreateInstance(type, new DetectionPlanGroup(new CameraRecordingSettings { DetectionMethod = DetectionMethods.Ai, AnalysisWidth = 320 }, null), null)!;
        var aiInput = new AnalysisFrame(new byte[1280 * 720 * 3], 1280, 720, true, DateTimeOffset.UnixEpoch);
        var aiOutput = (AnalysisFrame)resize.Invoke(aiWorker, [aiInput])!;
        Check(aiOutput.Width == 640 && aiOutput.Height == 360, "AI template resize respects its existing minimum input resolution", false);
        Check(true, "Template resizing matches the original scalar mapping for RGB, gray, odd sizes and shape changes");
    }

    private static void Regions()
    {
        const int width = 10, height = 6;
        var pixels = new bool[width * height];
        pixels[0] = pixels[10] = pixels[9] = pixels[19] = true;
        pixels[33] = true;
        var left = new MotionZone(0, 0, .5, 1, Name: "Left") { Id = "left" };
        var right = new MotionZone(.5, 0, .5, 1, Name: "Right") { Id = "right" };
        var masks = new Dictionary<MotionZone, bool[]>
        {
            [left] = Enumerable.Range(0, pixels.Length).Select(index => index % width < 5).ToArray(),
            [right] = Enumerable.Range(0, pixels.Length).Select(index => index % width >= 5).ToArray()
        };
        var extractor = new MotionRegionExtractor();
        var time = DateTimeOffset.UnixEpoch;
        var diagnostics = new AdaptiveMotionDiagnostics(2, 1, 2, 3, 0, 1, 0, "debouncing");
        var regions = extractor.Extract(pixels, width, height, pixels.Length, masks,
            [new("left", "Left", true, 3) { DebounceFrames = 3 }, new("right", "Right", false, .5)],
            true, time, 1.5, 2, pixels, new int[pixels.Length], 2, diagnostics);
        Check(regions.Length == 2 && regions.Single(region => region.Bounds.X == 0).Bounds == new DetectionBounds(0, 0, .1, 2.0 / height), "Region extraction preserves edges, four-connectivity, minimum size, and normalized geometry");
        var first = regions.Single(region => region.Zones.Single().Id == "left");
        Check(first.Confirmed && !regions.Single(region => region.Zones.Single().Id == "right").Confirmed && first.CapturedAt == time && first.ExpiresAt == time.AddSeconds(1.5), "Region qualification preserves zone scores, persistence, and expiry");
        Check(first.Sources[0].Attributes.Any(attribute => attribute.Name == "reason" && attribute.Text == "confirmed"), "Region diagnostics use the touched zone's policy");
        var saved = JsonSerializer.Serialize(regions);
        Check(extractor.Extract(new bool[pixels.Length], width, height, pixels.Length, [], [], false, time, 1.5).Length == 0 && JsonSerializer.Serialize(regions) == saved, "Scratch reuse cannot alter retained observations");
        Check(extractor.Extract(pixels, width, height, 0, [], [], true, time, 1, 2, new bool[pixels.Length]).All(region => !region.Confirmed), "Nonpersistent regions remain candidates even with global motion");
        Check(extractor.Extract(pixels, width, height, 0, [], [], false, time, 1, 2, diagnostics: diagnostics).All(region => region.Sources[0].Attributes.Any(attribute => attribute.Name == "reason" && attribute.Text == "debouncing")), "Global region diagnostics preserve debounce reasons and zero-area handling");
        var noise = new bool[80 * 40];
        for (var y = 0; y < 40; y += 3)
        {
            for (var x = 0; x < 80; x += 3)
            {
                noise[y * 80 + x] = true;
            }
        }
        Check(extractor.Extract(noise, 80, 40, noise.Length, [], [], true, time, 1, 1).Length == 64, "Region ranking retains the established display capacity in noisy scenes");
        for (var index = 0; index < 100; index++)
        {
            noise[index] = true;
        }
        Check(extractor.Extract(noise, 80, 40, noise.Length, [], [], true, time, 1, 1)[0].Sources[0].Attributes[0].Number >= 100, "A larger region replaces the smallest retained candidate");
        Check(extractor.Extract([], 0, 0, 0, [], [], false, time, 1).Length == 0, "Empty region input produces no observations");
    }

    private static void Images()
    {
        var random = new Random(409);
        var preparation = new AdaptiveFramePreparation();
        foreach (var (width, height) in new[] { (1, 1), (1, 5), (5, 1), (2, 2), (7, 5), (5, 7), (7, 5), (0, 0) })
        {
            var bytes = new byte[width * height];
            random.NextBytes(bytes);
            foreach (var included in new[] { new bool[bytes.Length], Enumerable.Repeat(true, bytes.Length).ToArray(), Enumerable.Range(0, bytes.Length).Select(_ => random.Next(2) == 1).ToArray() })
            {
                var expected = SmoothReference(bytes, width, height, included);
                Check(preparation.Prepare(bytes, width, height, included, true).SequenceEqual(expected), "Masked smoothing matches neighborhood sums at edges and after shape/mask changes", false);
                Check(preparation.Prepare(bytes, width, height, included, false).SequenceEqual(bytes), "Disabled smoothing is an exact passthrough", false);
            }
        }
        Check(true, "Smoothing covers empty/tiny shapes, excluded neighborhoods, edge pixels, rounding, and reused storage");
        var rgb = new AnalysisFrame([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0], 5, 1, true, DateTimeOffset.UnixEpoch);
        Check(rgb.Grayscale().SequenceEqual(new byte[] { 76, 149, 28, 255, 0 }), "Grayscale preserves exact RGB integer weights and endpoints");
        Check(ReferenceEquals((rgb with
        {
            IsRgb = false
        }).Grayscale(), rgb.Pixels), "Grayscale passthrough preserves buffer identity");
        foreach (var adapter in new YoloModelAdapter[] { new YoloxModelAdapter(), new UltralyticsModelAdapter() })
        {
            foreach (var (width, height, size) in new[] { (1, 1, 4), (3, 2, 5), (2, 3, 5), (7, 5, 3), (5, 7, 8), (8, 8, 8), (640, 360, 640) })
            {
                var bytes = new byte[width * height * 3];
                random.NextBytes(bytes);
                var frame = new AnalysisFrame(bytes, width, height, true, DateTimeOffset.UnixEpoch);
                var expected = PrepareReference(frame, size, adapter is UltralyticsModelAdapter);
                var actual = adapter.Prepare(frame, size);
                Check(actual.Values.SequenceEqual(expected.Values) && actual.Scale == expected.Scale && actual.Left == expected.Left && actual.Top == expected.Top, "Model input preserves bilinear rounding, channel order, odd letterbox padding, and identity scaling", false);
                var snapshot = actual.Values.ToArray();
                adapter.Prepare(frame with
                {
                    Pixels = new byte[bytes.Length]
                }, size);
                Check(actual.Values.SequenceEqual(snapshot), "Prepared tensor ownership survives a subsequent call", false);
            }
            try
            {
                adapter.Prepare(new([1, 2], 1, 1, true, DateTimeOffset.UnixEpoch), 4);
                throw new Exception("Truncated RGB accepted");
            }
            catch (InvalidOperationException)
            {
            }
        }
        var sharedAdapter = new UltralyticsModelAdapter();
        Parallel.For(0, 12, _ => Check(sharedAdapter.Prepare(rgb, 8).Values.SequenceEqual(PrepareReference(rgb, 8, true).Values), "Concurrent preprocessing keeps independent tensor ownership", false));
        Check(true, "AI preparation matches the original numeric contract for both adapters and rejects malformed RGB");
    }

    private static byte[] SmoothReference(byte[] pixels, int width, int height, bool[] included)
    {
        var result = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var neighbors = new List<byte>();
                for (var py = Math.Max(0, y - 1); py <= Math.Min(height - 1, y + 1); py++)
                {
                    for (var px = Math.Max(0, x - 1); px <= Math.Min(width - 1, x + 1); px++)
                    {
                        if (included[py * width + px])
                        {
                            neighbors.Add(pixels[py * width + px]);
                        }
                    }
                }
                result[y * width + x] = neighbors.Count == 0 ? pixels[y * width + x] : (byte)(neighbors.Sum(value => value) / neighbors.Count);
            }
        }
        return result;
    }

    private static PreparedImage PrepareReference(AnalysisFrame frame, int size, bool normalized)
    {
        var scale = Math.Min((double)size / frame.Width, (double)size / frame.Height);
        var width = Math.Max(1, (int)(frame.Width * scale));
        var height = Math.Max(1, (int)(frame.Height * scale));
        var left = normalized ? (size - width) / 2 : 0;
        var top = normalized ? (size - height) / 2 : 0;
        var output = Enumerable.Repeat(normalized ? 114f / 255 : 114f, size * size * 3).ToArray();
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + .5) * frame.Height / height - .5, 0, frame.Height - 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + .5) * frame.Width / width - .5, 0, frame.Width - 1);
                var x0 = (int)sx;
                var y0 = (int)sy;
                var fx = sx - x0;
                var fy = sy - y0;
                for (var channel = 0; channel < 3; channel++)
                {
                    var c = normalized ? channel : 2 - channel;
                    double Pixel(int px, int py) => frame.Pixels[(py * frame.Width + px) * 3 + c];
                    var value = (Pixel(x0, y0) * (1 - fx) + Pixel(Math.Min(x0 + 1, frame.Width - 1), y0) * fx) * (1 - fy) + (Pixel(x0, Math.Min(y0 + 1, frame.Height - 1)) * (1 - fx) + Pixel(Math.Min(x0 + 1, frame.Width - 1), Math.Min(y0 + 1, frame.Height - 1)) * fx) * fy;
                    output[channel * size * size + (y + top) * size + x + left] = (float)(Math.Round(value) / (normalized ? 255 : 1));
                }
            }
        }
        return new(output, scale, left, top, frame.Width, frame.Height);
    }

    private static async Task FragmentsAsync()
    {
        var movie = Movie();
        var first = Fragment(1000, 100);
        var second = Fragment(1100, 200);
        var payload = Box("mdat", Enumerable.Range(0, 1025).Select(index => (byte)index).ToArray());
        var bytes = Join(Box("mdat", [1]), movie, Box("free", new byte[31000]), first, payload, second, payload);
        for (var chunk = 1; chunk <= 64; chunk *= 4)
        {
            var fragments = new List<VideoFragment>();
            byte[]? header = null;
            await FragmentedMp4.ReadAsync(new ChunkStream(bytes, chunk), value => { header = value; return Task.CompletedTask; }, value => { fragments.Add(value); return Task.CompletedTask; }, CancellationToken.None);
            Check(header is not null && header.SequenceEqual(movie) && fragments.Count == 2 && fragments[0].Bytes.SequenceEqual(Join(first, payload)) && fragments[1].Bytes.SequenceEqual(Join(second, payload)), "Fragment assembly preserves metadata, payloads, and retained ownership across split input", false);
            Check(fragments[0].StartSeconds == 1 && fragments[1].StartSeconds == 1.1 && fragments[0].DurationSeconds == .1 && fragments[1].DurationSeconds == .2, "Fragment timing remains in the video clock", false);
            Check(fragments[0].PresentationEndSeconds == 1.1 && fragments[1].PresentationEndSeconds == 1.3 &&
                fragments.All(fragment => fragment.ProducedAt <= DateTimeOffset.UtcNow), "Presentation bounds and production timestamps survive fragmented pipe reads", false);
        }
        foreach (var composition in new[] { -50, 50 })
        {
            var timingFragments = new List<VideoFragment>();
            await FragmentedMp4.ReadAsync(new MemoryStream(Join(movie, Fragment(1000, 100, composition), payload)),
                _ => Task.CompletedTask, fragment => { timingFragments.Add(fragment); return Task.CompletedTask; }, CancellationToken.None);
            Check(timingFragments.Single().DurationSeconds == .1 &&
                Math.Abs(timingFragments.Single().PresentationEndSeconds!.Value - (1.1 + composition / 1000.0)) < 1e-9,
                "Signed and unsigned composition offsets preserve B-frame presentation timing", false);
        }
        foreach (var input in new[] { Box("free", [1, 2])[..^1], Box("ftyp", [1, 2])[..^1], Box("mdat", []) })
        {
            await FragmentedMp4.ReadAsync(new MemoryStream(input), _ => throw new Exception("Incomplete metadata published"), _ => throw new Exception("Incomplete fragment published"), CancellationToken.None);
        }
        foreach (var duration in new uint[] { 0, 31000 })
        {
            await ThrowsAsync<InvalidDataException>(() => FragmentedMp4.ReadAsync(new MemoryStream(Join(movie, Fragment(0, duration), payload)), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        }
        await ThrowsAsync<InvalidDataException>(() => FragmentedMp4.ReadAsync(new MemoryStream(Box("ftyp", new byte[1024 * 1024])), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<InvalidOperationException>(() => FragmentedMp4.ReadAsync(new MemoryStream(movie), _ => throw new InvalidOperationException(), _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<IOException>(() => FragmentedMp4.ReadAsync(new ThrowingStream(), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        var truncated = Join(movie, first, payload[..^1]);
        await FragmentedMp4.ReadAsync(new MemoryStream(truncated), _ => Task.CompletedTask, _ => throw new Exception("Truncated fragment published"), CancellationToken.None);
        for (var cut = 1; cut < 8; cut++)
        {
            await FragmentedMp4.ReadAsync(new MemoryStream(movie[..cut]), _ => throw new Exception("Partial metadata published"), _ => Task.CompletedTask, CancellationToken.None);
        }
        foreach (var size in new uint[] { 0, 7, FragmentedMp4.MaximumBoxBytes + 1 })
        {
            var prefix = Box("free", []);
            BinaryPrimitives.WriteUInt32BigEndian(prefix, size);
            await ThrowsAsync<InvalidDataException>(() => FragmentedMp4.ReadAsync(new MemoryStream(prefix), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        }
        await ThrowsAsync<InvalidDataException>(() => FragmentedMp4.ReadAsync(new MemoryStream(Box("moov", [])), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<OperationCanceledException>(() => FragmentedMp4.ReadAsync(new MemoryStream(bytes), _ => Task.CompletedTask, _ => Task.CompletedTask, new CancellationToken(true)));
        await ThrowsAsync<InvalidOperationException>(() => FragmentedMp4.ReadAsync(new MemoryStream(bytes), _ => Task.CompletedTask, _ => throw new InvalidOperationException(), CancellationToken.None));
        Check(true, "Fragment reader covers ignored boxes, early mdat, partial EOF, invalid lengths, malformed metadata, cancellation, and callback failure");
    }

    public static async Task FrameReaderAsync()
    {
        var type = typeof(AnalysisFrame).Assembly.GetType("SecurityCameraViewer.Recording.AnalysisFrameReader", throwOnError: true)!;
        var read = type.GetMethod("ReadAsync", BindingFlags.Static | BindingFlags.Public)!.CreateDelegate<Func<Stream, int, int, bool, Func<AnalysisFrame, Task>, CancellationToken, Task>>();
        var bytes = Enumerable.Range(0, 20).SelectMany(value => Enumerable.Repeat((byte)value, 18)).Concat(new byte[] { 255, 255 }).ToArray();
        var entered = new TaskCompletionSource<AnalysisFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copies = new List<byte[]>();
        async Task Consume(AnalysisFrame frame)
        {
            Check(frame.IsRgb && frame.Width == 3 && frame.Height == 2 && frame.Pixels.Length == 18, "Borrowed analysis buffers have exact dimensions", false);
            if (entered.TrySetResult(frame))
            {
                await release.Task;
            }
            copies.Add(frame.Pixels.ToArray());
        }
        var running = read(new DelayedStream(bytes), 3, 2, true, Consume, CancellationToken.None);
        var borrowed = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = borrowed.Pixels.ToArray();
        await Task.Delay(100);
        Check(borrowed.Pixels.SequenceEqual(snapshot), "Latest-frame replacement cannot reuse an actively borrowed frame");
        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Check(copies.Count >= 2 && copies[^1].All(value => value == 19), "Reader drains its final complete frame and excludes a partial tail");
        await ThrowsAsync<InvalidOperationException>(() => read(new DelayedStream(bytes), 3, 2, true, _ => throw new InvalidOperationException(), CancellationToken.None));
        await ThrowsAsync<OperationCanceledException>(() => read(new MemoryStream(bytes), 3, 2, true, _ => Task.CompletedTask, new CancellationToken(true)));
        await read(new MemoryStream(), 3, 2, false, _ => throw new Exception("Empty frame published"), CancellationToken.None);
        var gray = new List<byte[]>();
        await read(new ChunkStream([1, 2, 3, 4, 5, 6], 1), 3, 2, false, frame => { gray.Add(frame.Pixels.ToArray()); return Task.CompletedTask; }, CancellationToken.None);
        Check(gray.Single().SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "Grayscale analysis handles one-byte fragmented input");
        await ThrowsAsync<ArgumentOutOfRangeException>(() => read(new MemoryStream(), 0, 2, false, _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => read(new MemoryStream(), 2, -1, false, _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<OverflowException>(() => read(new MemoryStream(), int.MaxValue, 2, true, _ => Task.CompletedTask, CancellationToken.None));
        await ThrowsAsync<IOException>(() => read(new ThrowingStream(), 3, 2, true, _ => Task.CompletedTask, CancellationToken.None));
        var detector = new AdaptiveBackgroundDetector(new(), TimeZoneInfo.Utc);
        await ThrowsAsync<ArgumentException>(() => detector.AnalyseAsync(new([1], 3, 2, true, DateTimeOffset.UnixEpoch), 0, CancellationToken.None));
        Check(true, "Analysis reader releases buffers on EOF, cancellation, and consumer failure");
    }

    public static async Task ProcessFailuresAsync()
    {
        await ThrowsAsync<IOException>(() => new FfmpegProcess("missing-scv-test-ffmpeg").FileAsync([], CancellationToken.None));
        var process = new FfmpegProcess("ffmpeg");
        await ThrowsAsync<OperationCanceledException>(() => process.FileAsync([], new CancellationToken(true)));
        await ThrowsAsync<VideoSourceException>(() => process.RunAsync(["-f", "concat", "-safe", "0", "-i", "pipe:0", "-f", "null", "-"],
            "ffconcat version 1.0\nfile missing-scv-test-video.mp4\n", (stream, token) => stream.CopyToAsync(Stream.Null, token), CancellationToken.None));
        await ThrowsAsync<InvalidOperationException>(() => process.RunAsync(["-f", "lavfi", "-i", "testsrc2=size=16x16:rate=1", "-f", "null", "-"],
            null, (_, _) => throw new InvalidOperationException("Expected receiver failure"), CancellationToken.None));
        using var lifetime = new CancellationTokenSource();
        await ThrowsAsync<OperationCanceledException>(() => process.RunAsync(["-re", "-f", "lavfi", "-i", "testsrc2=size=16x16:rate=1", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"],
            null, async (stream, token) =>
            {
                await stream.ReadExactlyAsync(new byte[256], token);
                await lifetime.CancelAsync();
                await stream.ReadExactlyAsync(new byte[256], token);
            }, lifetime.Token));
        Check(true, "FFmpeg process startup, nonzero exit, receiver failure and active capture cancellation propagate after cleanup");
    }

    public static void Allocations()
    {
        long Extract(int width)
        {
            var pixels = new bool[width * width];
            pixels[0] = pixels[1] = pixels[width] = pixels[width + 1] = true;
            var extractor = new MotionRegionExtractor();
            for (var index = 0; index < 5; index++)
            {
                extractor.Extract(pixels, width, width, pixels.Length, [], [], true, DateTimeOffset.UnixEpoch, 1);
            }
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 5; index++)
            {
                extractor.Extract(pixels, width, width, pixels.Length, [], [], true, DateTimeOffset.UnixEpoch, 1);
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Check(Extract(320) == Extract(32), "Region extraction allocation does not grow with image area after scratch warmup");
        long Analyse(bool rgb)
        {
            var detector = new AdaptiveBackgroundDetector(new(), TimeZoneInfo.Utc);
            var frame = new AnalysisFrame(Enumerable.Repeat((byte)50, 80 * 45 * (rgb ? 3 : 1)).ToArray(), 80, 45, rgb, DateTimeOffset.UnixEpoch);
            for (var index = 0; index < 10; index++)
            {
                detector.AnalyseAsync(frame, index, CancellationToken.None).GetAwaiter().GetResult();
            }
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10; index++)
            {
                detector.AnalyseAsync(frame, 20 + index, CancellationToken.None).GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Check(Analyse(true) == Analyse(false), "Adaptive RGB analysis reuses grayscale storage after warmup");
    }

    private static byte[] Movie()
    {
        var track = Box("tkhd", new byte[20]);
        var clock = Box("mdhd", new byte[20]);
        var handler = Box("hdlr", new byte[12]);
        var defaults = Box("trex", new byte[24]);
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32BigEndian(clock.AsSpan(20), 1000);
        "vide"u8.CopyTo(handler.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(defaults.AsSpan(12), 1);
        return Join(Box("ftyp", [1, 2, 3, 4]), Box("moov", Join(Box("trak", Join(track, Box("mdia", Join(clock, handler)))), Box("mvex", defaults))));
    }

    private static byte[] Fragment(uint start, uint duration, int? composition = null)
    {
        var track = Box("tfhd", new byte[12]);
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(track.AsSpan(16), duration);
        var time = Box("tfdt", new byte[8]);
        BinaryPrimitives.WriteUInt32BigEndian(time.AsSpan(12), start);
        var samples = Box("trun", new byte[composition is null ? 8 : 12]);
        if (composition is not null)
        {
            BinaryPrimitives.WriteUInt32BigEndian(samples.AsSpan(8), (composition < 0 ? 0x01000000u : 0u) | 0x800u);
            BinaryPrimitives.WriteUInt32BigEndian(samples.AsSpan(16), unchecked((uint)composition.Value));
        }
        BinaryPrimitives.WriteUInt32BigEndian(samples.AsSpan(12), 1);
        return Box("moof", Box("traf", Join(track, time, samples)));
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var output = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(output, (uint)output.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(output, 4);
        payload.CopyTo(output, 8);
        return output;
    }

    private static byte[] Join(params byte[][] pieces) => pieces.SelectMany(piece => piece).ToArray();

    private static void Check(bool value, string message, bool print = true)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + message);
        }
        if (print)
        {
            Console.WriteLine("PASS: " + message);
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("Expected input failure");
    }

    private sealed class DelayedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(2, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
