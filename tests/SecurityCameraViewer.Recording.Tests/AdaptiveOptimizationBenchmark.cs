using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SecurityCameraViewer.Recording;

/// <summary>Fixed-input, synchronous diagnostics; the same test-first executable measures either runtime build.</summary>
internal static class AdaptiveOptimizationBenchmark
{
    public static void Run(string[] args)
    {
        var index = Array.IndexOf(args, "--adaptive-benchmark");
        var iterations = args.Length > index + 1 ? int.Parse(args[index + 1]) : 300;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
        const int warmup = 200;
        var results = new List<object>();
        foreach (var width in new[] { 320, 640, 960 })
        {
            var height = width * 9 / 16;
            var first = new byte[width * height];
            var second = new byte[first.Length];
            var rgb = new byte[first.Length * 3];
            var included = Enumerable.Repeat(true, first.Length).ToArray();
            var changed = new bool[first.Length];
            for (var i = 0; i < first.Length; i++)
            {
                first[i] = (byte)(40 + i % width * 160 / width);
                second[i] = (byte)(first[i] + (i % width > width / 3 && i % width < width / 2 && i / width > height / 3 && i / width < height / 2 ? 40 : 1));
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = first[i];
            }
            foreach (var mode in new[] { "mean", "robust", "noise", "shadows" })
            {
                var options = new AdaptiveMotionSettings { LearnNoise = mode == "noise", SuppressShadows = mode == "shadows" };
                var model = new AdaptiveBackgroundModel();
                model.Reset(first, rgb, options);
                if (mode is "mean" or "robust")
                {
                    Measure($"illumination-{mode}-{width}", step => GC.KeepAlive(model.Estimate(step % 2 == 0 ? first : second, included, mode == "robust")),
                        iterations, warmup, results);
                }
                else
                {
                    // Train noise once; subsequent frames keep the same sequence in both builds.
                    for (var step = 0; step < 20; step++)
                    {
                        model.Update(step % 2 == 0 ? first : second, rgb, included, changed, new(0, 1, first.Length), 8, true, options);
                    }
                }
                Measure($"background-{mode}-{width}", step => model.Update(step % 2 == 0 ? first : second, rgb, included, changed,
                    new(0, mode == "robust" ? 1.01 : 1, first.Length), 8, false, options), iterations, warmup, results);
            }
            foreach (var mode in new[] { "default", "zones", "persistence", "filtered" })
            {
                var settings = new CameraRecordingSettings
                {
                    Sensitivity = 100,
                    MinimumAreaPercent = .05,
                    Zones = mode == "zones" ? Enumerable.Range(0, 8).Select(i => new MotionZone(i / 8.0, 0, .125, 1) { Id = i.ToString() }).ToArray() : [],
                    Adaptive = new()
                    {
                        IncludeDiagnostics = true,
                        RegionPersistenceFrames = mode is "persistence" or "filtered" ? 3 : 1,
                        LearnNoise = mode == "filtered",
                        IlluminationCompensation = mode == "filtered" ? "robust" : "mean"
                    }
                };
                var detector = new AdaptiveBackgroundDetector(settings, TimeZoneInfo.Utc);
                Measure($"detector-{mode}-{width}", step => GC.KeepAlive(detector.Analyse(step % 2 == 0 ? first : second, width, height,
                    step * .2, DateTimeOffset.UnixEpoch.AddSeconds(step * .2))), iterations, warmup, results);
            }
        }
        var assembly = typeof(AdaptiveBackgroundDetector).Assembly.Location;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            vectorAccelerated = Vector.IsHardwareAccelerated,
            vectorFloatCount = Vector<float>.Count,
            recordingAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
            iterations,
            warmup,
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Measure(string name, Action<int> operation, int iterations, int warmup, List<object> results)
    {
        for (var step = 0; step < warmup; step++)
        {
            operation(step);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var step = warmup; step < warmup + iterations; step++)
        {
            operation(step);
        }
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        results.Add(new { name, millisecondsPerOperation = milliseconds, bytesPerOperation = (double)(GC.GetAllocatedBytesForCurrentThread() - allocated) / iterations });
    }
}
