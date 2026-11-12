using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SecurityCameraViewer.Core;

internal static class ProcessingMetricsTests
{
    public static async Task RunAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            ProcessingMetrics.Measure("test.inactive").Dispose();
            ProcessingMetrics.RecordFrame("test.inactive");
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            ProcessingMetrics.Measure("test.inactive").Dispose();
            ProcessingMetrics.RecordFrame("test.inactive");
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Inactive metrics do not allocate per operation");
        var inactive = ProcessingMetrics.Measure("test.inactive");
        var durations = new ConcurrentQueue<(string Stage, double Value)>();
        var counts = new ConcurrentQueue<(string Instrument, string Stage, long Value)>();
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == ProcessingMetrics.MeterName)
                {
                    Check(instrument.Meter.Version == "1.0.0" && instrument.Unit is not null, "Processing metrics have versioned names and units");
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, value, tags, _) => durations.Enqueue((Stage(tags), value)));
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => counts.Enqueue((instrument.Name, Stage(tags), value)));
            listener.Start();
            inactive.Dispose();
            default(ProcessingMetrics.Measurement).Dispose();
            Check(durations.IsEmpty && counts.IsEmpty, "Default and already inactive scopes remain inactive after attachment");
            using (ProcessingMetrics.Measure("test.success"))
            {
                ProcessingMetrics.RecordFrame("test.captured");
            }
            try
            {
                using var measurement = ProcessingMetrics.Measure("test.failure");
                throw new InvalidDataException("Expected test failure");
            }
            catch (InvalidDataException)
            {
            }
            try
            {
                using var measurement = ProcessingMetrics.Measure("test.cancelled");
                await Task.FromCanceled(new CancellationToken(true));
            }
            catch (OperationCanceledException)
            {
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                using var measurement = ProcessingMetrics.Measure("test.concurrent");
            })));
            Check(durations.Count(value => value.Stage == "test.concurrent") == 8 &&
                durations.All(value => double.IsFinite(value.Value) && value.Value >= 0), "Concurrent timers report finite nonnegative durations");
            foreach (var stage in new[] { "test.success", "test.failure", "test.cancelled" })
            {
                Check(durations.Count(value => value.Stage == stage) == 1 &&
                    counts.Count(value => value.Instrument == "scv.processing.operations" && value.Stage == stage && value.Value == 1) == 1,
                    "Timers record completion, failure, and cancellation once: " + stage);
            }
            Check(counts.Count(value => value.Instrument == "scv.frames" && value.Stage == "test.captured" && value.Value == 1) == 1,
                "Frame counters count pipeline stages independently of duration scopes");

            var images = new List<byte[]>();
            await using var stream = new ChunkStream([1, 0xff, 0xd8, 10, 0xff, 0xd9, 0xff, 0xd8, 20, 0xff, 0xd9, 0xff, 0xd8, 30]);
            await JpegFrames.ReadAsync(stream, images.Add, CancellationToken.None);
            Check(images.Count == 2 && images[0].SequenceEqual(new byte[] { 0xff, 0xd8, 10, 0xff, 0xd9 }) &&
                counts.Count(value => value.Instrument == "scv.frames" && value.Stage == "jpeg.completed") == 2,
                "Fragmented JPEG input counts complete frames and excludes unfinished frames");
            var oversized = new byte[JpegFrames.MaximumFrameBytes + 1];
            oversized[0] = 0xff;
            oversized[1] = 0xd8;
            try
            {
                await JpegFrames.ReadAsync(new MemoryStream(oversized), images.Add, CancellationToken.None);
                throw new Exception("Oversized JPEG unexpectedly accepted");
            }
            catch (InvalidDataException)
            {
                Check(images.Count == 2, "JPEG metrics preserve oversized-frame rejection without publishing a partial image");
            }
            var parsedChunks = durations.Count(value => value.Stage == "jpeg.parse_chunk");
            try
            {
                await JpegFrames.ReadAsync(new MemoryStream([0xff, 0xd8]), images.Add, new CancellationToken(true));
                throw new Exception("Cancelled JPEG input unexpectedly accepted");
            }
            catch (OperationCanceledException)
            {
                Check(durations.Count(value => value.Stage == "jpeg.parse_chunk") == parsedChunks,
                    "JPEG parsing durations exclude cancelled input waits");
            }
        }
        var detachedCount = durations.Count;
        ProcessingMetrics.Measure("test.detached").Dispose();
        Check(durations.Count == detachedCount, "Detaching the listener disables further measurements");

        foreach (var instrumentName in new[] { "scv.processing.operations", "scv.processing.duration" })
        {
            using var listener = new MeterListener();
            var received = 0;
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (instrument.Meter.Name == ProcessingMetrics.MeterName && instrument.Name == instrumentName)
                {
                    owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<double>((_, _, _, _) => received++);
            listener.SetMeasurementEventCallback<long>((_, _, _, _) => received++);
            listener.Start();
            ProcessingMetrics.Measure("test.single-instrument").Dispose();
            Check(received == 1, "Each duration or operations instrument can be subscribed independently");
        }
    }

    private static string Stage(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length != 1 || tags[0].Key != "stage" || tags[0].Value is not string stage)
        {
            throw new Exception("Processing measurements must expose only a stage label");
        }
        return stage;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }

    private sealed class ChunkStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
