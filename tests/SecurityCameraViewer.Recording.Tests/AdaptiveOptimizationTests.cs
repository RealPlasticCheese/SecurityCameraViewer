using System.Reflection;
using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class AdaptiveOptimizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        BackgroundSequences();
        NoiseBoundaries();
        Masks();
        Persistence();
        await DetectorSequencesAsync();
        await FailuresAsync();
    }

    private static void BackgroundSequences()
    {
        var random = new Random(710);
        foreach (var length in new[] { 0, 1, 7, 8, 15, 16, 31, 32, 33, 65, 257, 2049 })
        {
            foreach (var flags in Enumerable.Range(0, 8))
            {
                var options = new AdaptiveMotionSettings
                {
                    LearnNoise = (flags & 1) != 0,
                    SuppressShadows = (flags & 2) != 0,
                    IlluminationCompensation = (flags & 4) != 0 ? "robust" : "mean"
                };
                var actual = new AdaptiveBackgroundModel();
                var reference = new AdaptiveReference.AdaptiveBackgroundModel();
                var pixels = new byte[length];
                var rgb = new byte[length * 3];
                random.NextBytes(pixels);
                random.NextBytes(rgb);
                actual.Reset(pixels, rgb, options);
                reference.Reset(pixels, rgb, options);
                for (var frame = 0; frame < 45; frame++)
                {
                    random.NextBytes(pixels);
                    random.NextBytes(rgb);
                    var included = Enumerable.Range(0, length).Select(i => frame % 5 != 0 && (frame % 5 == 1 || i % 3 != 0)).ToArray();
                    var robust = options.IlluminationCompensation == "robust";
                    var estimate = actual.Estimate(pixels, included, robust);
                    var expected = reference.Estimate(pixels, included, robust);
                    Equal((estimate.Shift, estimate.Gain, estimate.IncludedPixels), (expected.Shift, expected.Gain, expected.IncludedPixels), "illumination arithmetic");
                    // Exercise exact lighting recovery boundaries and non-unit gains independently of estimation.
                    var shift = frame % 7 switch { 0 => 35, 1 => -35, 2 => 35.000001, 3 => -36, _ => estimate.Shift };
                    var gain = frame % 3 == 0 ? 1 : estimate.Gain;
                    var changed = new bool[length];
                    var expectedChanged = new bool[length];
                    Equal(actual.Update(pixels, rgb, included, changed, new(shift, gain, estimate.IncludedPixels), 8 + frame % 43,
                        frame < 10, options), reference.Update(pixels, rgb, included, expectedChanged, new(shift, gain, estimate.IncludedPixels),
                        8 + frame % 43, frame < 10, options), "shadow count");
                    Check(changed.SequenceEqual(expectedChanged), "foreground classifications");
                    CompareState(actual, reference);
                }
            }
        }
        Console.WriteLine("PASS: Background/illumination match frozen scalar reference, including all optional filters, empty inputs and vector tails");

        // The robust estimator must ignore prior foreground and retain its stable-pixel fallback.
        var model = new AdaptiveBackgroundModel();
        var baseline = new AdaptiveReference.AdaptiveBackgroundModel();
        var flat = Enumerable.Repeat((byte)100, 64).ToArray();
        var all = Enumerable.Repeat(true, 64).ToArray();
        model.Reset(flat, [], new());
        baseline.Reset(flat, [], new());
        foreach (var foregroundCount in new[] { 0, 49, 64 })
        {
            Field<bool>(model, "_foreground").AsSpan().Fill(false);
            Field<bool>(baseline, "_foreground").AsSpan().Fill(false);
            Field<bool>(model, "_foreground").AsSpan(0, foregroundCount).Fill(true);
            Field<bool>(baseline, "_foreground").AsSpan(0, foregroundCount).Fill(true);
            var a = model.Estimate(flat, all, true);
            var b = baseline.Estimate(flat, all, true);
            Equal((a.Shift, a.Gain, a.IncludedPixels), (b.Shift, b.Gain, b.IncludedPixels), "robust stable fallback");
        }
    }

    private static void NoiseBoundaries()
    {
        var actual = new AdaptiveBackgroundModel();
        var reference = new AdaptiveReference.AdaptiveBackgroundModel();
        foreach (var allowance in new[] { 1.0, 3.0, 6.0 })
        {
            var options = new AdaptiveMotionSettings { LearnNoise = true, NoiseAllowance = allowance };
            foreach (var variance in new[] { 0f, float.Epsilon, .01f, 7.111111f, 64f, 399.99997f, 400f })
            {
                foreach (var threshold in new[] { 8.0, 29.0, 50.0, 60.0, 61.0 })
                {
                    var boundary = Math.Max(threshold, Math.Min(60, allowance * Math.Sqrt(variance)));
                    foreach (var residual in new[] { Math.BitDecrement(boundary), boundary, Math.BitIncrement(boundary), -Math.BitDecrement(boundary), -boundary, -Math.BitIncrement(boundary) })
                    {
                        actual.Reset([0], [], options);
                        reference.Reset([0], [], options);
                        Field<float>(actual, "_variance")[0] = variance;
                        Field<float>(reference, "_variance")[0] = variance;
                        var changed = new bool[1];
                        var expected = new bool[1];
                        actual.Update([0], [], [true], changed, new(-residual, 1, 1), threshold, false, options);
                        reference.Update([0], [], [true], expected, new(-residual, 1, 1), threshold, false, options);
                        Check(changed.SequenceEqual(expected), $"noise threshold boundary {allowance}/{variance}/{threshold}/{residual:R}");
                        CompareState(actual, reference);
                    }
                }
            }
        }
        Console.WriteLine("PASS: Noise threshold equality, adjacent doubles, signed residuals, variance zero and 60-level cap are unchanged");
    }

    private static MotionZone[] Zones() =>
    [
        new(0, 0, .6, 1, Name: "Left") { Id = "left" },
        new(.4, 0, .6, 1, Times: [new("00:00", "00:01", [1])], Name: "Scheduled right") { Id = "right" },
        new(0, 0, 0, 0, Points: [new(.1, .1), new(.9, .1), new(.5, .9)], Name: "Triangle") { Id = "triangle" },
        new(.45, .2, .1, .6, true),
        new(0, .8, 1, .2, true, Times: [new("00:01", "00:02", [1])])
    ];

    private static void Masks()
    {
        foreach (var zones in new[] { Array.Empty<MotionZone>(), Zones(), new[] { Zones()[3] }, new[] { Zones()[1] },
            new[] { new MotionZone(0, 0, 1, 1, true) }, new[] { new MotionZone(0, 0, .001, .001) } })
        {
            var actual = new MotionRegionMask(zones, TimeZoneInfo.Utc);
            var reference = new AdaptiveReference.MotionRegionMask(zones, TimeZoneInfo.Utc);
            foreach (var (width, height, seconds) in new[] { (31, 17, 0), (31, 17, 1), (31, 17, 60), (31, 17, 61), (31, 17, 120), (3, 3, 121), (31, 17, 0), (0, 0, 0) })
            {
                actual.Update(width, height, Start.AddSeconds(seconds));
                reference.Update(width, height, Start.AddSeconds(seconds));
                Equal(actual.Revision, reference.Revision, "mask revision");
                Check(actual.Pixels.SequenceEqual(reference.Pixels), "mask pixels");
                Equal(actual.ZonePixels.Count, reference.ZonePixels.Count, "active zone count");
                foreach (var pair in reference.ZonePixels)
                {
                    Check(actual.ZonePixels[pair.Key].SequenceEqual(pair.Value), "zone membership and exclusion priority");
                }
                var retained = actual.Pixels;
                actual.Update(width, height, Start.AddSeconds(seconds));
                Check(ReferenceEquals(retained, actual.Pixels), "unchanged mask reuses storage");
            }
        }
        var scheduled = new[] { Zones()[1] };
        new MotionRegionMask([], TimeZoneInfo.Utc).Update(0, 0, Start);
        var localZone = TimeZoneInfo.CreateCustomTimeZone("Plus10", TimeSpan.FromHours(10), "Plus10", "Plus10");
        var localMask = new MotionRegionMask(scheduled, localZone);
        localMask.Update(3, 3, Start.AddHours(-10));
        Check(localMask.Pixels.Any(p => p), "schedules evaluate in configured local time");
        localMask.Update(3, 3, Start.AddHours(-10).AddMinutes(1));
        Check(!localMask.Pixels.Any(p => p), "scheduled inclusions do not enable full picture when inactive");
        Console.WriteLine("PASS: Masks preserve overlapping polygons, exclusions, empty areas, schedules, revision/reset rules and dimensions");
    }

    private static void Persistence()
    {
        var actual = new MotionRegionPersistence();
        var reference = new AdaptiveReference.MotionRegionPersistence();
        var random = new Random(134);
        foreach (var (width, height) in new[] { (1, 1), (9, 7), (61, 43), (0, 0) })
        {
            actual.Reset(width * height);
            reference.Reset(width * height);
            for (var frame = 0; frame < 80; frame++)
            {
                var spatial = Enumerable.Range(0, width * height).Select(i => frame % 9 != 0 &&
                    (frame % 7 == 0 ? i % width % 3 == 0 && i / width % 3 == 0 : random.Next(100) < 35)).ToArray();
                var result = Enumerable.Repeat(true, spatial.Length).ToArray();
                var expected = Enumerable.Repeat(true, spatial.Length).ToArray();
                var required = frame % 11 == 0 ? 1 : frame % 3 == 0 ? 10 : 2;
                var seconds = frame % 13 == 0 ? frame - 2 : frame * .2;
                actual.Apply(spatial, result, width, height, seconds, required, frame % 2 == 0 ? 5 : 1);
                reference.Apply(spatial, expected, width, height, seconds, required, frame % 2 == 0 ? 5 : 1);
                Check(result.SequenceEqual(expected) && actual.Progress.SequenceEqual(reference.Progress), "persistence matches ranked components and temporal matching");
                var extractor = new MotionRegionExtractor();
                var oldExtractor = new AdaptiveReference.MotionRegionExtractor();
                Equal(JsonSerializer.Serialize(extractor.Extract(spatial, width, height, spatial.Length, [], [], true, Start, 1.5,
                    1, result, actual.Progress, required, new(1, .1, 1, 1, 0, 1, 0, "confirmed"))),
                    JsonSerializer.Serialize(oldExtractor.Extract(spatial, width, height, spatial.Length, [], [], true, Start, 1.5,
                    1, expected, reference.Progress, required, new(1, .1, 1, 1, 0, 1, 0, "confirmed"))), "candidate ranking and evidence");
            }
        }
        Console.WriteLine("PASS: Persistence/results/evidence match through gaps, reverse time, mode changes, borders and more than 128 components");
        var candidate = new bool[25];
        candidate[6] = candidate[7] = candidate[11] = candidate[12] = true;
        var zone = new MotionZone(0, 0, 1, 1) { Id = "monitored" };
        var extractorWithZones = new MotionRegionExtractor();
        // Explicitly cover unconfirmed regions, missing diagnostic zone scores, null diagnostics and optional persistence.
        foreach (var score in new[] { Array.Empty<ZoneMotion>(), new[] { new ZoneMotion("elsewhere", "Other", false, .1) },
            new[] { new ZoneMotion(zone.Id, "Monitored", false, .1) },
            new[] { new ZoneMotion(zone.Id, "Monitored", false, 5) } })
        {
            foreach (var diagnostics in new AdaptiveMotionDiagnostics?[] { null, new(5, 1, 1, 3, 0, 1, 0, "debouncing") })
            {
                var observations = extractorWithZones.Extract(candidate, 5, 5, 25,
                    new() { [zone] = Enumerable.Repeat(true, 25).ToArray() }, score, false, Start, 1.5, 1, null, null, 1, diagnostics);
                Check(observations.Length == 1 && !observations[0].Confirmed, "unconfirmed diagnostic candidate");
            }
        }
    }

    private static async Task DetectorSequencesAsync()
    {
        var random = new Random(1093);
        var cases = 0;
        foreach (var flags in Enumerable.Range(0, 32))
        {
            foreach (var zoneMode in Enumerable.Range(0, 3))
            {
                var settings = new CameraRecordingSettings
                {
                    Sensitivity = flags % 2 == 0 ? 100 : 60,
                    MinimumAreaPercent = flags % 3 == 0 ? 1 : .05,
                    Zones = zoneMode == 0 ? [] : zoneMode == 1 ? Zones() : [new(0, 0, 1, 1, true)],
                    Adaptive = new()
                    {
                        LearnNoise = (flags & 1) != 0,
                        SuppressShadows = (flags & 2) != 0,
                        SmoothFrames = (flags & 4) != 0,
                        IlluminationCompensation = (flags & 8) != 0 ? "robust" : "mean",
                        RegionPersistenceFrames = (flags & 16) != 0 ? 3 : 1,
                        ConfirmationFrames = flags % 2 == 0 ? 1 : 3,
                        CandidateMinimumPixels = flags % 3 == 0 ? 20 : 4,
                        IncludeDiagnostics = true
                    }
                };
                var actual = new AdaptiveBackgroundDetector(settings, TimeZoneInfo.Utc);
                var reference = new AdaptiveReference.AdaptiveBackgroundDetector(settings, TimeZoneInfo.Utc);
                MotionSample retained = default;
                string? retainedJson = null;
                for (var frame = 0; frame < 145; frame++)
                {
                    var width = frame < 120 ? 41 : 17;
                    var height = frame < 120 ? 23 : 11;
                    var pixels = new byte[width * height * 3];
                    for (var y = 0; y < height; y++)
                    {
                        for (var x = 0; x < width; x++)
                        {
                            var value = 50 + x * 2 + (frame % 29 == 0 ? 45 : 0) + random.Next(-3, 4);
                            if (frame >= 12 && x >= 5 + frame / 20 % 4 && x < width / 2 && y >= 3 && y < height - 4)
                            {
                                value += frame % 17 == 0 ? -30 : 90;
                            }
                            for (var channel = 0; channel < 3; channel++)
                            {
                                pixels[(y * width + x) * 3 + channel] = (byte)Math.Clamp(value + channel * 12, 0, 255);
                            }
                        }
                    }
                    var seconds = frame == 85 ? 14 : frame * .2;
                    var time = Start.AddSeconds(frame * .2 + (frame >= 100 ? 60 : 0));
                    var input = new AnalysisFrame(pixels, width, height, true, time);
                    var result = await actual.AnalyseAsync(input, seconds, CancellationToken.None);
                    var expected = await reference.AnalyseAsync(input, seconds, CancellationToken.None);
                    Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(expected), $"complete detector flags={flags}, zones={zoneMode}, frame={frame}");
                    if (retainedJson is not null)
                    {
                        Equal(JsonSerializer.Serialize(retained), retainedJson, "returned observations remain independently owned");
                    }
                    retained = result;
                    retainedJson = JsonSerializer.Serialize(result);
                    cases++;
                }
            }
        }
        Console.WriteLine($"PASS: {cases:N0} complete detector results match before-change reference, including diagnostics, zone debounce and retained observations");
    }

    private static async Task FailuresAsync()
    {
        var detector = new AdaptiveBackgroundDetector(new(), TimeZoneInfo.Utc);
        foreach (var (pixels, width, height) in new[] { (new byte[9], 2, 3), (new byte[9], 3, 2), (new byte[8], 3, 3) })
        {
            Throws<ArgumentException>(() => detector.Analyse(pixels, width, height, 0, Start));
        }
        Throws<OverflowException>(() => detector.Analyse([], int.MaxValue, 3, 0, Start));
        await ThrowsAsync<ArgumentException>(() => detector.AnalyseAsync(new(new byte[8], 3, 3, false, Start), 0, CancellationToken.None));
        await ThrowsAsync<ArgumentException>(() => detector.AnalyseAsync(new(new byte[26], 3, 3, true, Start), 0, CancellationToken.None));
        await ThrowsAsync<OperationCanceledException>(() => detector.AnalyseAsync(new(new byte[9], 3, 3, false, Start), 0, new(true)));
        var shadows = new AdaptiveBackgroundDetector(new() { Adaptive = new() { SuppressShadows = true } }, TimeZoneInfo.Utc);
        Throws<InvalidOperationException>(() => shadows.Analyse(new byte[9], 3, 3, 0, Start));
        // Exercise gray passthrough after RGB reuse and allocation after an RGB resolution change.
        await detector.AnalyseAsync(new(new byte[27], 3, 3, true, Start), 0, CancellationToken.None);
        await detector.AnalyseAsync(new(new byte[9], 3, 3, false, Start), 2, CancellationToken.None);
        await detector.AnalyseAsync(new(new byte[45], 5, 3, true, Start), 3, CancellationToken.None);
        Console.WriteLine("PASS: Invalid shapes/formats, overflow, missing shadow colour and cancellation retain their failure contracts");
    }

    private static T[] Field<T>(object target, string name) => (T[])target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void CompareState(object actual, object reference)
    {
        foreach (var field in new[] { "_background", "_variance", "_color" })
        {
            Check(Field<float>(actual, field).SequenceEqual(Field<float>(reference, field)), "exact model state " + field);
        }
        Check(Field<bool>(actual, "_foreground").SequenceEqual(Field<bool>(reference, "_foreground")), "foreground model state");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
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

    private static void Equal<T>(T actual, T expected, string name) => Check(EqualityComparer<T>.Default.Equals(actual, expected), name + $": {actual} != {expected}");

    private static void Check(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }
    }
}
