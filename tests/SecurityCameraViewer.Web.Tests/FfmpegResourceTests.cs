using System.Diagnostics.Metrics;
using System.Diagnostics;
using SecurityCameraViewer.Core;

internal static class FfmpegResourceTests
{
    public static void Run()
    {
        using var meter = new Meter("test.ffmpeg.resources");
        var resources = new FfmpegResourceMetrics(meter);
        var reads = 0;
        var sample = new FfmpegResources(2, 1024);
        using var first = resources.Track("preview", 1, () => { reads++; return sample; });
        LivePerformanceTests.Check(reads == 0, "Unobserved native metrics perform no process queries");
        var values = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter == meter)
            {
                observer.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            LivePerformanceTests.Check(tags.Length == 2 && (string)tags[0].Value! == "preview" && (int)tags[1].Value! == 1,
                "Native metrics contain only pipeline and channel tags");
            values[instrument.Name] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        LivePerformanceTests.Check(values["scv.ffmpeg.cpu.time"] == 2 && values["scv.ffmpeg.working_set"] == 1024 &&
            values["scv.ffmpeg.processes"] == 1, "Native metrics expose cumulative CPU, resident memory and child count");
        sample = new(3, 2048);
        first.Dispose();
        first.Dispose();
        listener.RecordObservableInstruments();
        LivePerformanceTests.Check(values["scv.ffmpeg.cpu.time"] == 3 && values["scv.ffmpeg.working_set"] == 0 &&
            values["scv.ffmpeg.processes"] == 0, "Exited children retain CPU totals without retaining memory or process count");
        using var restarted = resources.Track("preview", 1, () => new(1, 512));
        using var unavailable = resources.Track("preview", 1, () => null);
        listener.RecordObservableInstruments();
        LivePerformanceTests.Check(values["scv.ffmpeg.cpu.time"] == 4 && values["scv.ffmpeg.working_set"] == 512 &&
            values["scv.ffmpeg.processes"] == 2, "Restarts accumulate CPU and unavailable resource samples do not break collection");
        using (var process = Process.GetCurrentProcess())
        using (resources.Track(process, "preview", 1))
        {
            listener.RecordObservableInstruments();
            LivePerformanceTests.Check(values["scv.ffmpeg.working_set"] > 512 && values["scv.ffmpeg.processes"] == 3,
                "Real OS process resources can be sampled alongside other children");
        }
        using (var exited = new Process())
        using (resources.Track(exited, "preview", 1))
        {
            listener.RecordObservableInstruments();
            LivePerformanceTests.Check(values["scv.ffmpeg.working_set"] == 512,
                "A child becoming unavailable to the OS cannot break the metrics callback");
        }
        listener.Dispose();
        var before = reads;
        using var unobserved = resources.Track("analysis", 2, () => { reads++; return new(1, 100); });
        unobserved.Dispose();
        LivePerformanceTests.Check(reads == before, "Detached collectors leave native resource probes disabled");
    }
}
