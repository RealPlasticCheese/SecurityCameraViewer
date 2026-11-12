using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class DetectionObservationTests
{
    public static void Run()
    {
        const int width = 960, height = 540;
        var settings = new CameraRecordingSettings { Channel = 1, MinimumAreaPercent = 0.1 };
        var detector = new AdaptiveBackgroundDetector(settings, TimeZoneInfo.Utc);
        var pixels = Enumerable.Repeat((byte)50, width * height).ToArray();
        var start = DateTimeOffset.UtcNow;
        Check(detector.Analyse(pixels, width, height, 0, start).Detections.Length == 0, "Warmup has no detection boxes");
        detector.Analyse(pixels, width, height, 3, start.AddSeconds(3));
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 10; column++)
            {
                Paint(column * 20 + 10, row * 20 + 10, 5, 5);
            }
        }
        Paint(400, 300, 30, 20);
        MotionSample sample = default;
        for (var i = 0; i < 3; i++)
        {
            sample = detector.Analyse(pixels, width, height, 4 + i * 0.2, start.AddSeconds(4 + i * 0.2));
        }
        Check(sample.Detected && sample.Detections.Length == 64, "Motion extraction bounds noisy scenes to 64 components without changing the trigger");
        var largest = sample.Detections[0];
        Check(largest.Bounds == new DetectionBounds(400.0 / width, 300.0 / height, 30.0 / width, 20.0 / height),
            "Largest connected region is retained with normalized bounds");
        Check(largest.Confirmed && largest.Sources.Single().Method == DetectionMethods.Adaptive &&
            largest.Sources.Single().Attributes.Single(value => value.Name == "movingArea").Number > 0 &&
            largest.Sources.Single().Attributes.All(value => value.Name != "confidence"),
            "Motion evidence reports changed pixels without inventing object confidence");
        Check(largest.ExpiresAt > largest.CapturedAt && JsonSerializer.Serialize(largest).Contains("movingPixels"),
            "Generic observations serialize typed metadata and bounded freshness");

        var excluded = new AdaptiveBackgroundDetector(settings with
        {
            Zones = [new(0, 0, 1, 1, true)]
        }, TimeZoneInfo.Utc);
        excluded.Analyse(pixels, width, height, 0, start);
        Check(excluded.Analyse(pixels, width, height, 4, start.AddSeconds(4)).Detections.Length == 0,
            "Excluded areas produce no motion overlay boxes");

        var driveway = new MotionZone(0.4, 0.5, 0.2, 0.2, Name: "Driveway") { Id = Guid.NewGuid().ToString("N") };
        var candidates = new AdaptiveBackgroundDetector(settings with
        {
            MinimumAreaPercent = 20,
            Zones = [driveway]
        }, TimeZoneInfo.Utc);
        var baseline = Enumerable.Repeat((byte)50, width * height).ToArray();
        candidates.Analyse(baseline, width, height, 0, start);
        var candidate = candidates.Analyse(pixels, width, height, 3, start.AddSeconds(3));
        Check(!candidate.Detected && candidate.Detections.Length == 1 && !candidate.Detections[0].Confirmed &&
            candidate.Detections[0].Zones.Single().Id == driveway.Id,
            "Small movement below a zone threshold remains a candidate with its associated zone");
        Array.Fill(baseline, (byte)200);
        Check(candidates.Analyse(baseline, width, height, 3.2, start.AddSeconds(3.2)).Detections.Length == 0,
            "An illumination jump clears motion observations instead of outlining the whole picture");

        void Paint(int x, int y, int w, int h)
        {
            for (var py = y; py < y + h; py++)
            {
                Array.Fill(pixels, (byte)210, py * width + x, w);
            }
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
    }
}
