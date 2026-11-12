using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class AiDetectionTests
{
    public static async Task RunAsync(string root)
    {
        var legacy = JsonSerializer.Deserialize<CameraRecordingSettings>("{\"Channel\":1}")!;
        Check(legacy.DetectionMethod == DetectionMethods.Adaptive && legacy.AnalysisWidth == 320 && legacy.AnalysisFramesPerSecond == 5,
            "Legacy camera settings preserve adaptive defaults");
        var valid = new RecordingSettings { Directory = root, Cameras = [legacy] };
        var registry = new AiModelRegistry(root);
        Check(valid.Validate(registry) is null, "New detection defaults validate");
        foreach (var invalid in new[]
        {
            legacy with { DetectionMethod = "unknown" }, legacy with { AiModel = "unknown" },
            legacy with { AiDevice = "unknown" }, legacy with { AnalysisWidth = 2000 },
            legacy with { AnalysisFramesPerSecond = double.NaN }, legacy with { AiFramesPerSecond = 100 },
            legacy with { AiConfidence = 0 }, legacy with { AiClasses = [] }, legacy with { AiClasses = ["unsupported-object"] },
            legacy with { AiIgnoreClasses = ["person"] }, legacy with { AiOptions = new() { ["maxDetections"] = 1.5 } },
            legacy with { MotionGateMinimumAreaPercent = 0 }
        })
        {
            Check((valid with { Cameras = [invalid] }).Validate(registry) is not null, "Invalid detector settings are rejected");
        }

        var frame = Frame();
        var now = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        var previousSelection = JsonSerializer.Deserialize<CameraRecordingSettings>("{\"Channel\":1,\"AiClasses\":[\"person\",\"car\"]}")!;
        Check(previousSelection.AiClasses.SequenceEqual(["person", "car"]), "Explicit saved object rules remain unchanged by the broader defaults");
        foreach (var vehicle in new[] { "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat" })
        {
            var vehicleInference = new FakeInference { Objects = [new(vehicle, 0.9, 0.2, 0.2, 0.1, 0.1)] };
            using var vehicleDetector = new AiMotionDetector(legacy with
            {
                DetectionMethod = DetectionMethods.Ai
            }, TimeZoneInfo.Utc, vehicleInference);
            var vehicleSample = await vehicleDetector.AnalyseAsync(frame with
            {
                CapturedAt = now
            }, 0, CancellationToken.None);
            Check(vehicleSample.Detected && vehicleSample.Detections!.Single().Disposition == "trigger",
                $"Default object rules let {vehicle} qualify automatic actions and appear as triggering in overlays");
        }
        var left = new MotionZone(0, 0, 0.5, 1, Name: "Left") { Id = Guid.NewGuid().ToString("N") };
        var right = new MotionZone(0.5, 0, 0.5, 1, Name: "Right") { Id = Guid.NewGuid().ToString("N") };
        var inference = new FakeInference();
        var settings = legacy with
        {
            DetectionMethod = DetectionMethods.Hybrid,
            Zones = [left, right]
        };
        using var hybrid = new AiMotionDetector(settings, TimeZoneInfo.Utc, inference);
        for (var i = 0; i < 20; i++)
        {
            Check(!(await hybrid.AnalyseAsync(frame with
            {
                CapturedAt = now
            }, i * 0.2, CancellationToken.None)).Detected,
                "Idle hybrid analysis does not trigger");
        }

        Check(inference.Calls == 0, "Hybrid mode performs no inference without pixel motion");
        var moving = Frame();
        Paint(moving, 40, 8, 12, 12);
        inference.Objects = [new("person", 0.9, 0.1, 0.2, 0.2, 0.4)];
        MotionSample sample = default;
        for (var i = 0; i < 4; i++)
        {
            sample = await hybrid.AnalyseAsync(moving with
            {
                CapturedAt = now
            }, 4 + i * 0.2, CancellationToken.None);
        }

        Check(inference.Calls == 1 && !sample.Detected, "Motion in one zone cannot confirm an object in another zone");
        Check(sample.Detections.Single(item => item.Kind == "object").Confirmed == false &&
            sample.Detections.Single(item => item.Kind == "object").Sources.All(source => source.Method != DetectionMethods.Adaptive),
            "Ungated AI objects stay candidates without claiming another zone's motion evidence");
        inference.Objects = [new("car", 0.9, 0.65, 0.2, 0.2, 0.4)];
        sample = await hybrid.AnalyseAsync(moving with
        {
            CapturedAt = now
        }, 5.6, CancellationToken.None);
        Check(sample.Detected && sample.Zones!.Single(zone => zone.Detected).Id == right.Id,
            "Hybrid AI confirms the matching moving zone");
        var confirmedObject = sample.Detections.Single(item => item.Kind == "object" && item.Confirmed);
        Check(confirmedObject.Sources.Select(source => source.Method).SequenceEqual([DetectionMethods.Adaptive, DetectionMethods.Ai]) &&
            confirmedObject.Zones.Single().Id == right.Id && confirmedObject.Sources[1].Model == "yolox-nano" &&
            confirmedObject.Sources[1].Attributes.Any(value => value.Name == "objectType" && value.Text == "car"),
            "Hybrid observations preserve both methods, object metadata and the matching zone");
        Check(sample.Detections.Where(item => item.Kind == "motion").All(item => !item.Confirmed),
            "Hybrid pixel boxes remain motion-gate candidates independently of confirmed AI objects");
        sample = await hybrid.AnalyseAsync(frame with
        {
            CapturedAt = now
        }, 6, CancellationToken.None);
        Check(sample.Detected, "Hybrid gate retains a bounded confirmation tail after motion stops");
        sample = await hybrid.AnalyseAsync(frame with
        {
            CapturedAt = now
        }, 10, CancellationToken.None);
        Check(!sample.Detected, "Hybrid gate expires even when an object remains present");

        var aiSettings = legacy with
        {
            DetectionMethod = DetectionMethods.Ai,
            Zones = [right]
        };
        var aiInference = new FakeInference { Objects = [new("car", 0.9, 0.65, 0.2, 0.2, 0.4)] };
        using var ai = new AiMotionDetector(aiSettings, TimeZoneInfo.Utc, aiInference);
        for (var i = 0; i < 5; i++)
        {
            sample = await ai.AnalyseAsync(frame with
            {
                CapturedAt = now.AddSeconds(i * 0.2)
            }, i * 0.2, CancellationToken.None);
            Check(sample.Detected,
                "AI-only mode detects stationary object presence");
        }

        Check(aiInference.Calls == 1, "AI rate limits repeated checks and retains fresh results between frames");
        Check(sample.Detections.Single().CapturedAt == now && sample.Detections.Single().ExpiresAt == now.AddSeconds(2) &&
            sample.Detections.Single().Sources.Single().Attributes.Any(value => value.Name == "confidence" && value.Number == 0.9),
            "Cached AI boxes keep their original timestamp, expiry and typed confidence");
        aiInference.Failed = true;
        sample = await ai.AnalyseAsync(frame with
        {
            CapturedAt = now
        }, 1.2, CancellationToken.None);
        Check(!sample.Detected && sample.Ai?.Failed == true, "AI failure suppresses automatic motion and reports status");
        Check(sample.Detections.Length == 0, "AI errors clear object observations");

        foreach (var method in new[] { DetectionMethods.Ai, DetectionMethods.Hybrid })
        {
            var ignoredInference = new FakeInference
            {
                Objects = [new("dog", 0.95, 0.65, 0.2, 0.2, 0.4), new("person", 0.9, 0.65, 0.2, 0.2, 0.4)]
            };
            using var rules = new AiMotionDetector(aiSettings with
            {
                DetectionMethod = method,
                AiClasses = ["person"],
                AiIgnoreClasses = ["dog"]
            }, TimeZoneInfo.Utc, ignoredInference);
            await rules.AnalyseAsync(frame with
            {
                CapturedAt = now
            }, 0, CancellationToken.None);
            for (var i = 0; i < 4; i++)
            {
                sample = await rules.AnalyseAsync(moving with
                {
                    CapturedAt = now.AddSeconds(3 + i * 0.2)
                }, 3 + i * 0.2, CancellationToken.None);
            }
            Check(sample.Detected && sample.Detections.Any(item => item.Kind == "object" && item.Disposition == "trigger" && item.Confirmed) &&
                sample.Detections.Any(item => item.Kind == "object" && item.Disposition == "ignore" && !item.Confirmed),
                $"{method}: a triggering person remains active beside an ignored dog, with distinct overlay rules");
            ignoredInference.Objects = [new("dog", 0.95, 0.65, 0.2, 0.2, 0.4)];
            sample = await rules.AnalyseAsync(moving with
            {
                CapturedAt = now.AddSeconds(5)
            }, 5, CancellationToken.None);
            Check(!sample.Detected && sample.Zones!.All(zone => !zone.Detected) &&
                sample.Detections.Where(item => item.Kind == "object").All(item => item.Disposition == "ignore"),
                $"{method}: ignored objects cannot trigger or extend automatic actions");
        }

        using var ignoreOnly = new AiMotionDetector(aiSettings with
        {
            AiClasses = [],
            AiIgnoreClasses = ["dog"]
        }, TimeZoneInfo.Utc,
            new FakeInference { Objects = [new("dog", 0.95, 0.65, 0.2, 0.2, 0.4)] });
        var ignoredOnly = await ignoreOnly.AnalyseAsync(frame, 0, CancellationToken.None);
        Check(!ignoredOnly.Detected && ignoredOnly.Detections.Single().Disposition == "ignore",
            "Ignore-only object rules support diagnostic overlays without automatic triggers");

        var exclusion = new MotionZone(0.6, 0, 0.4, 1, Exclude: true);
        using var excluded = new AiMotionDetector(aiSettings with
        {
            Zones = [right, exclusion]
        }, TimeZoneInfo.Utc,
            new FakeInference { Objects = [new("car", 0.9, 0.65, 0.2, 0.2, 0.4)] });
        var excludedSample = await excluded.AnalyseAsync(frame, 0, CancellationToken.None);
        Check(!excludedSample.Detected && excludedSample.Detections.Length == 0, "AI object centres obey exclusion masks for triggers and overlays");
        var inactiveInference = new FakeInference();
        using var scheduled = new AiMotionDetector(aiSettings with
        {
            Zones = [right with { Times = [new("08:00", "09:00")] }]
        }, TimeZoneInfo.Utc, inactiveInference);
        sample = await scheduled.AnalyseAsync(frame with
        {
            CapturedAt = now
        }, 0, CancellationToken.None);
        Check(!sample.ScheduleActive && inactiveInference.Calls == 0, "Inactive AI schedules suppress inference");

        var adapter = new UltralyticsModelAdapter();
        var input = adapter.Prepare(frame, 640);
        var output = new ModelTensor([100, 150, 300, 350, 0.9f, 0, 100, 150, 300, 350, 0.8f, 0], [1, 2, 6]);
        var decoded = adapter.Decode(output, input, 640, 0.45, ["person"]);
        Check(decoded.Length == 1 && decoded[0].Class == "person" && decoded[0].X >= 0,
            "End-to-end outputs restore letterbox coordinates and suppress duplicate boxes");
        Check(adapter.Decode(output, input, 640, 0.45, ["car"]).Length == 0, "Object class selections filter detections");
        var raw = new float[84];
        raw[0] = 200;
        raw[1] = 250;
        raw[2] = 100;
        raw[3] = 100;
        raw[4] = 0.9f;
        raw[4 + 16] = 0.99f;
        Check(adapter.Decode(new(raw, [1, 84, 1]), input, 640, 0.45, ["person"]).Length == 0,
            "Raw outputs cannot reclassify a winning dog as a lower-scoring person");
        raw[4 + 16] = 0;
        Check(adapter.Decode(new(raw, [1, 84, 1]), input, 640, 0.45, ["person"]).Length == 1,
            "Raw channel-major COCO outputs identify selected objects");
        var rgb = new AnalysisFrame([255, 0, 0], 1, 1, true, now);
        Check(new YoloxModelAdapter().Prepare(rgb, 416).Values[0] == 0 &&
            new YoloxModelAdapter().Prepare(rgb, 416).Values[2 * 416 * 416] == 255 &&
            adapter.Prepare(rgb, 640).Values[0] == 1, "Model adapters use correct BGR/RGB ordering and normalization");

        var directory = Path.Combine(root, "fake-models");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "yolo26n.onnx"), "fake");
        var factory = new FakeSessions();
        using var service = new AiInferenceService(directory, factory);
        var modelSettings = aiSettings with
        {
            AiModel = "yolo26-nano"
        };
        var analyses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.AnalyseAsync(frame, modelSettings, CancellationToken.None))));
        Check(factory.Created == 1 && factory.MaximumConcurrent == 1 && analyses.All(result => !result.Status.Failed),
            "Multiple cameras share one model and a bounded inference lane");
        var gpu = await service.AnalyseAsync(frame, modelSettings with
        {
            AiDevice = "gpu"
        }, CancellationToken.None);
        Check(gpu.Status.Failed && factory.Created == 1, "Explicit unavailable GPU does not silently use CPU");
        factory.Gpus = ["GPU.0"];
        factory.FailGpu = true;
        using var automatic = new AiInferenceService(directory, factory);
        var fallback = await automatic.AnalyseAsync(frame, modelSettings with
        {
            AiDevice = "auto"
        }, CancellationToken.None);
        Check(!fallback.Status.Failed && fallback.Status.Device == "cpu" && fallback.Status.Message.Contains("GPU"),
            "Automatic reports CPU fallback when GPU cannot load the model");
        await automatic.AnalyseAsync(frame, modelSettings with
        {
            AiDevice = "auto"
        }, CancellationToken.None);
        Check(factory.GpuAttempts == 1, "Failed GPU model loads back off while Automatic continues on CPU");
        var missing = await service.AnalyseAsync(frame, modelSettings with
        {
            AiModel = "yolox-nano"
        }, CancellationToken.None);
        Check(missing.Status.Failed && missing.Status.Message.Contains("not installed"), "Missing models report installation guidance");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await service.AnalyseAsync(frame, modelSettings, cancellation.Token);
            throw new Exception("Cancelled inference unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PASS: Inference respects cancellation");
        }

        Check(new ConfigurableMotionDetectorFactory(inference).Create(legacy, TimeZoneInfo.Utc) is AdaptiveBackgroundDetector,
            "Factory preserves adaptive selection");
    }

    public static async Task SmokeAsync(string directory, string rgbPath, bool openVino)
    {
        var bytes = await File.ReadAllBytesAsync(rgbPath);
        var frame = new AnalysisFrame(bytes, 640, 360, true, DateTimeOffset.UtcNow);
        var models = AiModelCatalog.All.Where(model => File.Exists(Path.Combine(directory, model.FileName))).ToArray();
        Check(models.Length > 0, "Smoke test has at least one installed model");
        foreach (var model in models)
        {
            IObjectModelAdapter adapter = model.Adapter == "ultralytics" ? new UltralyticsModelAdapter() : new YoloxModelAdapter();
            var input = adapter.Prepare(frame, model.InputSize);
            using IModelSession session = openVino
                ? new OpenVinoModelSession(Path.Combine(directory, model.FileName), model.InputSize, "CPU")
                : new OnnxModelSession(Path.Combine(directory, model.FileName), model.InputSize);
            var output = session.Run(input.Values, model.InputSize, CancellationToken.None);
            var objects = adapter.Decode(output, input, model.InputSize, 0.3, AiModelCatalog.Classes.Values.ToArray());
            Console.WriteLine($"{model.Id}: output [{string.Join(',', output.Dimensions)}]; {string.Join(", ", objects.Select(item => $"{item.Class} {item.Confidence:P0}"))}");
            Check(objects.Any(item => item.Class == "person"), $"Real {model.Id} model identifies people in the sample image ({(openVino ? "OpenVINO" : "ONNX Runtime")})");
        }
    }

    private static AnalysisFrame Frame() => new(Enumerable.Repeat((byte)50, 64 * 36 * 3).ToArray(), 64, 36, true, DateTimeOffset.UtcNow);

    private static void Paint(AnalysisFrame frame, int x, int y, int width, int height)
    {
        for (var py = y; py < y + height; py++)
        {
            for (var px = x; px < x + width; px++)
            {
                Array.Fill(frame.Pixels, (byte)210, (py * frame.Width + px) * 3, 3);
            }
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + message);
        }

        Console.WriteLine("PASS: " + message);
    }

    private sealed class FakeInference : IAiInferenceService
    {
        public int Calls;
        public bool Failed;
        public DetectedObject[] Objects = [];

        public Task<ObjectAnalysis> AnalyseAsync(AnalysisFrame frame, CameraRecordingSettings settings, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new ObjectAnalysis(Objects, new(settings.AiModel, "cpu", 1, "Test inference", Failed)));
        }
    }

    private sealed class FakeSessions : IModelSessionFactory
    {
        public int Created;
        public int Concurrent;
        public int MaximumConcurrent;
        public string[] Gpus = [];
        public bool FailGpu;
        public int GpuAttempts;

        public string[] GpuDevices() => Gpus;

        public IModelSession Create(string path, AiModelDefinition model, string device)
        {
            if (device == "gpu")
            {
                GpuAttempts++;
            }

            if (device == "gpu" && FailGpu)
            {
                throw new InvalidOperationException("Test GPU failure");
            }

            Created++;
            return new Session(this);
        }

        private sealed class Session(FakeSessions owner) : IModelSession
        {
            public ModelTensor Run(float[] input, int size, CancellationToken token)
            {
                var concurrent = Interlocked.Increment(ref owner.Concurrent);
                owner.MaximumConcurrent = Math.Max(owner.MaximumConcurrent, concurrent);
                try
                {
                    Thread.Sleep(10);
                    return new([100, 150, 300, 350, 0.9f, 0], [1, 1, 6]);
                }
                finally
                {
                    Interlocked.Decrement(ref owner.Concurrent);
                }
            }

            public void Dispose()
            {
            }
        }
    }
}
