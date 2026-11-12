using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class AdaptiveMotionTests
{
    private const int Width = 80, Height = 45;
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync(string root)
    {
        var legacy = JsonSerializer.Deserialize<CameraRecordingSettings>("{\"Channel\":1}")!;
        Check(legacy.Adaptive == new AdaptiveMotionSettings(), "Legacy adaptive filters remain disabled with three-frame confirmation");
        var settings = new RecordingSettings { Directory = root, Cameras = [legacy, legacy with { Channel = 2 }] };
        var migrated = DetectionTemplateResolver.Migrate(settings);
        Check(migrated.DetectionTemplates.Length == 1 && migrated.Cameras.All(camera => camera.DetectionTemplateId == migrated.DetectionTemplates[0].Id),
            "Identical legacy detection policies migrate to one reusable template");
        Check(DetectionTemplateResolver.Migrate(settings).DetectionTemplates[0].Id == migrated.DetectionTemplates[0].Id &&
            DetectionTemplateResolver.Migrate(migrated).DetectionTemplates.Length == 1, "Template migration is deterministic and idempotent");
        Check(DetectionTemplateResolver.Migrate(settings with
        {
            Cameras = [legacy, legacy with { Channel = 2, Sensitivity = 90 }]
        }).DetectionTemplates.Length == 2,
            "Migration preserves independently tuned cameras");
        var roundTrip = JsonSerializer.Deserialize<RecordingSettings>(JsonSerializer.Serialize(migrated))!;
        Check(roundTrip.Validate() is null && roundTrip.Cameras[0].DetectionTemplateId == migrated.Cameras[0].DetectionTemplateId,
            "Template definitions and assignments survive persistence");
        Check((migrated with { DetectionTemplates = [] }).Validate() is not null, "Referenced templates cannot be removed");
        var largeCollection = DetectionTemplateResolver.Migrate(settings with
        {
            Cameras = Enumerable.Range(1, 65).Select(channel => legacy with { Channel = channel, Sensitivity = channel }).ToArray()
        });
        Check(largeCollection.Validate() is null && largeCollection.DetectionTemplates.Length == 64 &&
            largeCollection.Cameras[64].DetectionTemplateId is null &&
            DetectionTemplateResolver.Resolve(largeCollection, largeCollection.Cameras[64]).Groups[0].Settings.Sensitivity == 65,
            "Template migration preserves excess legacy policies in larger camera collections");
        foreach (var starter in DetectionTemplateCatalog.Create())
        {
            Check(starter.IsValid && starter.Settings.Validate(new AiModelRegistry(root)) is null, $"Starter template validates: {starter.Name}");
        }
        foreach (var invalid in new[]
        {
            new AdaptiveMotionSettings { NoiseAllowance = double.NaN }, new() { RegionPersistenceFrames = 0 },
            new() { ConfirmationFrames = 11 }, new() { CandidateMinimumPixels = 3 }, new() { IlluminationCompensation = "invalid" },
            new() { ShadowMinimumBrightnessRatio = 0 }, new() { ShadowColorTolerance = double.PositiveInfinity }
        })
        {
            Check(invalid.Validate() is not null, "Invalid adaptive options are rejected");
        }
        var policy = legacy with
        {
            Sensitivity = 90,
            MinimumAreaPercent = 0.1,
            Adaptive = new()
            {
                IncludeDiagnostics = true
            }
        };
        var ordinary = Warm(policy);
        var display = Warm(policy with
        {
            Adaptive = policy.Adaptive with
            {
                CandidateMinimumPixels = 10000
            }
        });
        var moving = Gray();
        Paint(moving, 10, 10, 8, 8, 200);
        var sample = Analyse(ordinary, moving, 3);
        Check(!sample.Detected && sample.Detections.Length > 0 && sample.AdaptiveDiagnostics[0].Reason == "debouncing",
            "Diagnostics explain an unconfirmed region and its moving-area threshold");
        Analyse(ordinary, moving, 3.2);
        sample = Analyse(ordinary, moving, 3.4);
        Analyse(display, moving, 3);
        Analyse(display, moving, 3.2);
        var hidden = Analyse(display, moving, 3.4);
        Check(sample.Detected && hidden.Detected && hidden.Detections.Length == 0,
            "Candidate display filtering cannot change motion qualification");
        Check(sample.Detections[0].Sources[0].Attributes.Any(attribute => attribute.Name == "confirmationProgress" && attribute.Text == "3/3"),
            "Overlay evidence includes confirmation progress");

        var persistent = Warm(policy with
        {
            Adaptive = policy.Adaptive with
            {
                RegionPersistenceFrames = 2,
                ConfirmationFrames = 1
            }
        });
        for (var frame = 0; frame < 6; frame++)
        {
            var noise = Gray();
            Paint(noise, frame % 2 == 0 ? 10 : 55, 10, 7, 7, 200);
            Check(!Analyse(persistent, noise, 3 + frame * 0.2).Detected, "Unrelated artifact regions cannot accumulate temporal persistence");
        }
        persistent = Warm(policy with
        {
            Adaptive = policy.Adaptive with
            {
                RegionPersistenceFrames = 2,
                ConfirmationFrames = 1
            }
        });
        Check(!Analyse(persistent, moving, 3).Detected && Analyse(persistent, moving, 3.2).Detected,
            "A recurring coherent region survives temporal filtering");
        Check(!Analyse(persistent, moving, 5).Detected, "A frame gap resets region persistence");

        var learnedPolicy = policy with
        {
            Sensitivity = 100,
            Adaptive = new()
            {
                LearnNoise = true,
                ConfirmationFrames = 1
            }
        };
        var learned = new AdaptiveBackgroundDetector(learnedPolicy, TimeZoneInfo.Utc);
        var fixedThreshold = new AdaptiveBackgroundDetector(learnedPolicy with
        {
            Adaptive = new()
            {
                ConfirmationFrames = 1
            }
        }, TimeZoneInfo.Utc);
        for (var frame = 0; frame < 10; frame++)
        {
            var noise = Gray();
            Paint(noise, 10, 10, 12, 12, (byte)(frame % 2 == 0 ? 35 : 65));
            Analyse(learned, noise, frame * 0.2);
            Analyse(fixedThreshold, noise, frame * 0.2);
        }
        var fluctuating = Gray();
        Paint(fluctuating, 10, 10, 12, 12, 65);
        Check(!Analyse(learned, fluctuating, 2.2).Detected && Analyse(fixedThreshold, fluctuating, 2.2).Detected,
            "Learned local variation rejects recurring background flicker");
        Paint(fluctuating, 45, 10, 12, 12, 220);
        Check(Analyse(learned, fluctuating, 2.4).Detected, "Noise learning preserves a new high-contrast foreground subject");

        var robust = Warm(policy with
        {
            Adaptive = new()
            {
                IlluminationCompensation = "robust",
                ConfirmationFrames = 1
            }
        });
        var largeSubject = Gray();
        Paint(largeSubject, 8, 8, 42, 28, 210);
        Check(Analyse(robust, largeSubject, 3).Detected, "Robust lighting estimation is not overwhelmed by a large foreground subject");
        var contrastBase = Gray();
        for (var i = 0; i < contrastBase.Length; i++)
        {
            contrastBase[i] = (byte)(30 + i % Width * 2);
        }
        robust = Warm(policy with
        {
            Adaptive = new()
            {
                IlluminationCompensation = "robust",
                ConfirmationFrames = 1
            }
        }, contrastBase);
        var contrast = contrastBase.Select(pixel => (byte)(pixel * 1.15 + 10)).ToArray();
        Check(!Analyse(robust, contrast, 3).Detected, "Robust compensation rejects a global brightness and contrast change");

        var mask = new MotionZone(0, 0, 0.25, 1, true);
        var smoothed = Warm(policy with
        {
            Zones = [mask],
            Adaptive = new()
            {
                SmoothFrames = true,
                ConfirmationFrames = 1
            }
        });
        var timestamp = Gray();
        Paint(timestamp, 0, 0, 20, Height, 255);
        Check(!Analyse(smoothed, timestamp, 3).Detected, "Smoothing cannot leak excluded timestamps into monitored pixels");
        var pixelNoise = Gray();
        Paint(pixelNoise, 35, 15, 2, 2, 75);
        smoothed = Warm(policy with
        {
            Adaptive = new()
            {
                SmoothFrames = true,
                ConfirmationFrames = 1
            }
        });
        var unsmoothed = Warm(policy with
        {
            Adaptive = new()
            {
                ConfirmationFrames = 1
            }
        });
        Check(!Analyse(smoothed, pixelNoise, 3).Detected && Analyse(unsmoothed, pixelNoise, 3).Detected,
            "Light smoothing suppresses small compression artifacts");

        var colorPolicy = policy with
        {
            Adaptive = new()
            {
                SuppressShadows = true,
                ConfirmationFrames = 1,
                IncludeDiagnostics = true
            }
        };
        var colors = ColorFrame();
        var shadows = new AdaptiveBackgroundDetector(colorPolicy, TimeZoneInfo.Utc);
        await shadows.AnalyseAsync(colors, 0, CancellationToken.None);
        await shadows.AnalyseAsync(colors, 2.5, CancellationToken.None);
        var shadow = ColorFrame();
        PaintColor(shadow.Pixels, 10, 10, 12, 12, 70, 105, 140);
        sample = await shadows.AnalyseAsync(shadow, 3, CancellationToken.None);
        Check(!sample.Detected && sample.AdaptiveDiagnostics[0].SuppressedShadowPixels > 0, "Colour-preserving darkening is rejected as a likely shadow");
        var subject = ColorFrame();
        PaintColor(subject.Pixels, 40, 10, 12, 12, 200, 40, 40);
        Check((await shadows.AnalyseAsync(subject, 3.2, CancellationToken.None)).Detected, "A changed-colour foreground subject survives shadow filtering");
        try
        {
            shadows.Analyse(Gray(), Width, Height, 4, Start);
            throw new Exception("Missing colour frames were silently accepted.");
        }
        catch (InvalidOperationException)
        {
            Check(true, "Shadow filtering explicitly requires colour analysis");
        }
        await TemplatesAsync(migrated);
    }

    private static async Task TemplatesAsync(RecordingSettings settings)
    {
        var left = new MotionZone(0, 0, 0.5, 1, Name: "Left") { Id = Guid.NewGuid().ToString("N") };
        var fast = new DetectionTemplate(Guid.NewGuid().ToString("N"), "Fast detail", new()
        {
            AnalysisWidth = 640,
            AnalysisFramesPerSecond = 10,
            Adaptive = new()
            {
                SuppressShadows = true
            }
        });
        var right = new MotionZone(0.5, 0, 0.5, 1, Name: "Right")
        {
            Id = Guid.NewGuid().ToString("N"),
            DetectionTemplateId = fast.Id
        };
        var camera = settings.Cameras[0] with
        {
            Zones = [left, right, new(0, 0, 0.1, 0.1, true)]
        };
        settings = settings with
        {
            Cameras = [camera],
            DetectionTemplates = [.. settings.DetectionTemplates, fast]
        };
        Check(settings.Validate() is null, "Camera defaults and zone template overrides validate");
        Check((settings with { Cameras = [camera with { Zones = [right with { Exclude = true }] }] }).Validate() is not null,
            "Exclusion zones cannot override detection templates");
        var plan = DetectionTemplateResolver.Resolve(settings, camera);
        Check(plan.Groups.Length == 2 && plan.SourceSettings.AnalysisWidth == 640 && plan.SourceSettings.AnalysisFramesPerSecond == 10 && plan.SourceSettings.Adaptive.SuppressShadows,
            "Shared capture uses the resolution, rate and colour required by all zone templates");
        Check(plan.Groups.All(group => group.Settings.Zones.Any(zone => zone.Exclude)) && plan.Groups[0].Settings.Zones.Count(zone => !zone.Exclude) == 1,
            "Every template retains exclusions and only its own inclusion zones");
        var factory = new TestFactory();
        using (var detector = (IDisposable)plan.Create(factory, TimeZoneInfo.Utc))
        {
            var frame = new AnalysisFrame(new byte[640 * 360 * 3], 640, 360, true, Start);
            var analysis = (IFrameMotionDetector)detector;
            await analysis.AnalyseAsync(frame, 0, CancellationToken.None);
            await analysis.AnalyseAsync(frame, 0.1, CancellationToken.None);
            var result = await analysis.AnalyseAsync(frame, 0.2, CancellationToken.None);
            Check(factory.Workers[0].Calls == 2 && factory.Workers[0].Width == 320 && factory.Workers[1].Calls == 3 && factory.Workers[1].Width == 640,
                "Templates retain independent analysis resolutions and frame rates on shared capture");
            Check(result.Zones!.Select(zone => zone.Id).Order().SequenceEqual(new[] { left.Id, right.Id }.Order()),
                "Composite analysis preserves zone identities for actions and recording");
        }
        Check(factory.Workers.All(worker => worker.Disposed), "Template worker resources are disposed together");
        camera = camera with
        {
            Zones = [left, right with { DetectionTemplateId = null }]
        };
        Check(DetectionTemplateResolver.Resolve(settings, camera).Groups.Length == 1, "Zones sharing one template share one detector worker");
    }

    private sealed class TestFactory : IMotionDetectorFactory
    {
        public string Name => "Test";
        public List<TestDetector> Workers { get; } = [];
        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone)
        {
            var worker = new TestDetector(settings);
            Workers.Add(worker);
            return worker;
        }
    }
    private sealed class TestDetector(CameraRecordingSettings settings) : IAsyncMotionDetector
    {
        public int Calls
        {
            get; private set;
        }
        public int Width
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public Task<MotionSample> AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token)
        {
            Calls++;
            Width = frame.Width;
            return Task.FromResult(new MotionSample(true, 1, false, true,
                settings.Zones.Where(zone => !zone.Exclude).Select(zone => new ZoneMotion(zone.Id, zone.Name, true, 1)).ToArray()));
        }
        public void Dispose() => Disposed = true;
    }
    private static byte[] Gray() => Enumerable.Repeat((byte)50, Width * Height).ToArray();
    private static MotionSample Analyse(AdaptiveBackgroundDetector detector, byte[] pixels, double seconds) =>
        detector.Analyse(pixels, Width, Height, seconds, Start.AddSeconds(seconds));
    private static AdaptiveBackgroundDetector Warm(CameraRecordingSettings policy, byte[]? baseline = null)
    {
        var detector = new AdaptiveBackgroundDetector(policy, TimeZoneInfo.Utc);
        Analyse(detector, baseline ?? Gray(), 0);
        Analyse(detector, baseline ?? Gray(), 2.5);
        return detector;
    }
    private static void Paint(byte[] pixels, int x, int y, int width, int height, byte value)
    {
        for (var row = y; row < y + height; row++)
        {
            Array.Fill(pixels, value, row * Width + x, width);
        }
    }
    private static AnalysisFrame ColorFrame()
    {
        var pixels = new byte[Width * Height * 3];
        PaintColor(pixels, 0, 0, Width, Height, 100, 150, 200);
        return new(pixels, Width, Height, true, Start);
    }
    private static void PaintColor(byte[] pixels, int x, int y, int width, int height, byte red, byte green, byte blue)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var index = (row * Width + column) * 3;
                pixels[index] = red;
                pixels[index + 1] = green;
                pixels[index + 2] = blue;
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
}
