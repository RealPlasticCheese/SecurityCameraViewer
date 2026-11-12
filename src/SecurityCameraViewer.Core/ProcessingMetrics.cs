using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SecurityCameraViewer.Core;

/// <summary>Listener-enabled processing measurements. Stage labels describe code, never camera identities or credentials.</summary>
public static class ProcessingMetrics
{
    public const string MeterName = "SecurityCameraViewer.Processing";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "scv.processing.duration", "ms", "Elapsed stage time, including failed or cancelled work.");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "scv.processing.operations", "{operation}", "Finished stage scopes, including failed or cancelled work.");
    private static readonly Counter<long> Frames = Meter.CreateCounter<long>(
        "scv.frames", "{frame}", "Frames captured, dequeued, dropped, or completed at a pipeline stage.");
    private static readonly FfmpegResourceMetrics NativeResources = new(Meter);

    public static IDisposable TrackFfmpeg(Process process, string pipeline, int channel) =>
        NativeResources.Track(process, pipeline, channel);

    public static Measurement Measure(string stage) => Duration.Enabled || Operations.Enabled
        ? new(stage, Duration.Enabled ? Stopwatch.GetTimestamp() : 0) : default;

    public static void RecordFrame(string stage)
    {
        if (Frames.Enabled)
        {
            Frames.Add(1, new KeyValuePair<string, object?>("stage", stage));
        }
    }

    /// <summary>A value scope; dispose once. An inactive scope does not read the clock or allocate.</summary>
    public readonly struct Measurement : IDisposable
    {
        private readonly string? _stage;
        private readonly long _started;

        internal Measurement(string stage, long started)
        {
            _stage = stage;
            _started = started;
        }

        public void Dispose()
        {
            if (_stage is null)
            {
                return;
            }

            var tag = new KeyValuePair<string, object?>("stage", _stage);
            if (_started != 0 && Duration.Enabled)
            {
                Duration.Record(Stopwatch.GetElapsedTime(_started).TotalMilliseconds, tag);
            }
            if (Operations.Enabled)
            {
                Operations.Add(1, tag);
            }
        }
    }
}
