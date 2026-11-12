using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

internal static class ProcessingStageTests
{
    public static async Task RunAsync(string root)
    {
        var expectedMotion = MotionSamples();
        var stages = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == ProcessingMetrics.MeterName && instrument.Name == "scv.processing.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            Check(double.IsFinite(value) && value >= 0 && tags.Length == 1, "Stage timings contain only finite durations and a stage label", false);
            stages.Enqueue((string)tags[0].Value!);
        });
        listener.Start();
        Check(MotionSamples() == expectedMotion, "Attaching metrics preserves adaptive detection, regions, and diagnostics");
        foreach (var stage in new[] { "image.grayscale", "motion.total", "motion.smooth", "motion.illumination", "motion.background", "motion.persistence", "motion.regions" })
        {
            Check(stages.Contains(stage), "Adaptive processing exposes stage: " + stage);
        }
        var grayStages = stages.Count(stage => stage == "image.grayscale");
        var gray = new AnalysisFrame([50, 50, 50, 50], 2, 2, false, DateTimeOffset.UnixEpoch);
        Check(ReferenceEquals(gray.Grayscale(), gray.Pixels) && stages.Count(stage => stage == "image.grayscale") == grayStages,
            "Already grayscale frames bypass conversion and conversion timing");

        var settings = new CameraRecordingSettings();
        var templateDetector = DetectionTemplateResolver.Resolve(new RecordingSettings(), settings)
            .Create(new AdaptiveBackgroundDetectorFactory(), TimeZoneInfo.Utc);
        using (templateDetector as IDisposable)
        {
            await templateDetector.AnalyseAsync(new(new byte[640 * 360], 640, 360, false, DateTimeOffset.UnixEpoch), 0, CancellationToken.None);
            Check(stages.Contains("image.resize"), "Template workers expose their per-frame resize cost");
        }

        var directory = Path.Combine(root, "profiling-models");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "yolo26n.onnx"), "Test session factory bypasses native model loading");
        var factory = new Sessions();
        using var service = new AiInferenceService(directory, factory);
        settings = settings with
        {
            AiModel = "yolo26-nano",
            DetectionMethod = DetectionMethods.Ai
        };
        var frame = new AnalysisFrame([255, 0, 0], 1, 1, true, DateTimeOffset.UnixEpoch);
        var first = Task.Run(() => service.AnalyseAsync(frame, settings, CancellationToken.None));
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var waiting = service.AnalyseAsync(frame, settings, cancellation.Token);
        cancellation.Cancel();
        try
        {
            await waiting;
            throw new Exception("Cancelled AI wait unexpectedly completed");
        }
        catch (OperationCanceledException)
        {
            Check(stages.Contains("ai.wait"), "Cancelled shared-inference waits are timed without swallowing cancellation");
        }
        finally
        {
            factory.Release.Set();
        }
        var result = await first;
        Check(!result.Status.Failed && result.Objects.Single().Class == "person", "AI measurements preserve decoded output");
        foreach (var stage in new[] { "ai.wait", "ai.prepare", "ai.session", "ai.inference", "ai.decode" })
        {
            Check(stages.Contains(stage), "AI processing exposes stage: " + stage);
        }
        var inferenceCount = stages.Count(stage => stage == "ai.inference");
        factory.Fail = true;
        result = await service.AnalyseAsync(frame, settings, CancellationToken.None);
        Check(result.Status.Failed && stages.Count(stage => stage == "ai.inference") == inferenceCount + 1,
            "Failed native inference is timed and retains the existing failure response");
        factory.Fail = false;
        inferenceCount = stages.Count(stage => stage == "ai.inference");
        result = await service.AnalyseAsync(frame, settings with
        {
            AiModel = "yolox-nano"
        }, CancellationToken.None);
        Check(result.Status.Failed && stages.Count(stage => stage == "ai.inference") == inferenceCount,
            "Missing-model setup cost is distinct from native inference");
        var decodeCount = stages.Count(stage => stage == "ai.decode");
        try
        {
            new UltralyticsModelAdapter().Decode(new([], []), new([], 1, 0, 0, 1, 1), 640, 0.5, ["person"]);
            throw new Exception("Malformed model output unexpectedly accepted");
        }
        catch (InvalidOperationException)
        {
            Check(stages.Count(stage => stage == "ai.decode") == decodeCount + 1, "Malformed model output retains rejection and records decoding cost");
        }
        factory.Release.Dispose();
    }

    private static string MotionSamples()
    {
        var settings = new CameraRecordingSettings
        {
            Sensitivity = 90,
            MinimumAreaPercent = 0.1,
            Adaptive = new()
            {
                SmoothFrames = true,
                LearnNoise = true,
                SuppressShadows = true,
                IlluminationCompensation = "robust",
                RegionPersistenceFrames = 2,
                IncludeDiagnostics = true
            }
        };
        var detector = new AdaptiveBackgroundDetector(settings, TimeZoneInfo.Utc);
        var pixels = Enumerable.Repeat((byte)50, 64 * 36 * 3).ToArray();
        var samples = new List<MotionSample>();
        for (var index = 0; index < 20; index++)
        {
            if (index >= 14)
            {
                for (var y = 10; y < 20; y++)
                {
                    for (var x = 10; x < 20; x++)
                    {
                        Array.Fill(pixels, (byte)200, (y * 64 + x) * 3, 3);
                    }
                }
            }
            samples.Add(detector.AnalyseAsync(new(pixels, 64, 36, true, DateTimeOffset.UnixEpoch), index * 0.2, CancellationToken.None).GetAwaiter().GetResult());
        }
        return JsonSerializer.Serialize(samples);
    }

    private static void Check(bool condition, string message, bool print = true)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        if (print)
        {
            Console.WriteLine("PASS: " + message);
        }
    }

    private sealed class Sessions : IModelSessionFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public bool Fail
        {
            get; set;
        }
        public string[] GpuDevices() => [];
        public IModelSession Create(string path, AiModelDefinition model, string device) => new Session(this);

        private sealed class Session(Sessions owner) : IModelSession
        {
            public ModelTensor Run(float[] input, int size, CancellationToken token)
            {
                owner.Entered.TrySetResult();
                owner.Release.Wait(token);
                if (owner.Fail)
                {
                    throw new InvalidOperationException("Expected native inference failure");
                }
                return new([100, 150, 300, 350, 0.9f, 0], [1, 1, 6]);
            }
            public void Dispose()
            {
            }
        }
    }
}
