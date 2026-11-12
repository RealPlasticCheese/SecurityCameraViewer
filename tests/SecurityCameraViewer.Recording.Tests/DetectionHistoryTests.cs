using SecurityCameraViewer.Recording;

internal static class DetectionHistoryTests
{
    public static void Run()
    {
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        var observation = new DetectionObservation(new(0.1, 0.2, 0.3, 0.4), "motion", [], [], true, now, now.AddSeconds(2));
        var history = new DetectionHistory(new() { Seconds = 10, MaximumSamplesPerCamera = 3 });
        var input = new[] { observation };
        history.Add(now, input, now);
        input[0] = observation with { Confirmed = false };
        history.Add(now.AddSeconds(1), [], now.AddSeconds(1));
        var snapshot = history.Snapshot(now.AddSeconds(1));
        Check(snapshot.Frames.Length == 2 && snapshot.Frames[0].Detections[0].Confirmed &&
            snapshot.Frames[1].Detections.Length == 0, "History preserves observations and empty samples without retaining caller arrays");
        Check(snapshot.Frames[0].Detections[0].ExpiresAt < now.AddSeconds(3) && history.Snapshot(now.AddSeconds(3)).Frames.Length == 2,
            "Wall-clock expiry does not discard results needed by delayed footage");

        history.Add(now.AddSeconds(2), [observation], now.AddSeconds(4));
        Check(history.Snapshot(now.AddSeconds(3)).Frames.Length == 2 &&
            history.Snapshot(now.AddSeconds(4), now.AddSeconds(3)).Frames.Single().CapturedAt == now.AddSeconds(2),
            "Incremental history uses publication time so late inference results are not lost");
        Check(history.Snapshot(now.AddSeconds(4), now.AddSeconds(4)).Frames.Length == 1,
            "Publication cursor boundaries are inclusive for safe deduplication");
        history.Add(now.AddSeconds(3), [], now.AddSeconds(5));
        Check(history.Snapshot(now.AddSeconds(5)).Frames.Length == 3 &&
            history.Snapshot(now.AddSeconds(5)).Frames[0].CapturedAt == now.AddSeconds(1),
            "Configured sample budget evicts the oldest metadata including empty frames");
        Check(history.Snapshot(now.AddSeconds(16)).Frames.Length == 0, "Configured duration expires idle camera history");

        history.Add(now.AddSeconds(4), [], now.AddSeconds(17));
        var previousId = history.Snapshot(now.AddSeconds(17)).Id;
        history.Add(now, [observation], now.AddSeconds(18));
        snapshot = history.Snapshot(now.AddSeconds(18));
        Check(snapshot.Id != previousId && snapshot.Frames.Length == 1, "A backwards capture clock begins a clean history epoch");
        Check(history.Snapshot(now.AddSeconds(18), now.AddSeconds(30)).Frames.Length == 1,
            "A cursor ahead of the server clock recovers with a full retained snapshot");
        history.Clear();
        Check(history.Snapshot(now.AddSeconds(18)).Id != snapshot.Id && history.Snapshot(now.AddSeconds(18)).Frames.Length == 0,
            "Reconnect and configuration resets clear history and change its identity");

        foreach (var options in new DetectionHistoryOptions[]
        {
            new() { Seconds = 0 }, new() { Seconds = -1 }, new() { Seconds = double.NaN },
            new() { Seconds = double.PositiveInfinity }, new() { MaximumSamplesPerCamera = 0 }, new() { MaximumSamplesPerCamera = -1 }
        })
        {
            try
            {
                _ = new DetectionHistory(options);
                throw new Exception("Invalid history budget accepted.");
            }
            catch (ArgumentException)
            {
            }
        }
        Check(true, "Invalid duration and resource budgets are rejected without imposing a history ceiling");

        var concurrent = new DetectionHistory(new() { MaximumSamplesPerCamera = 20 });
        Parallel.For(0, 100, index =>
        {
            concurrent.Add(now, [], now);
            _ = concurrent.Snapshot(now);
        });
        Check(concurrent.Snapshot(now).Frames.Length == 20, "Concurrent polling and publication preserve the configured metadata budget");
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
