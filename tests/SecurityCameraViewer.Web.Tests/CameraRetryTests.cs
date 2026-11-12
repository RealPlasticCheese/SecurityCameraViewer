using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class CameraRetryTests
{
    public static async Task RunAsync()
    {
        ScheduleValidation();
        RecoveryDecisions();
        await RetryCancellationAsync();
        await PersistenceAsync();
        BrowserRetryConfiguration();
        await PlaybackRetriesAsync();
    }

    private static void BrowserRetryConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scv-browser-retries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["settings"] = Path.Combine(directory, "settings.json")
            }).Build();
            using var settingsStore = new JsonAppSettingsStore(configuration);
            var store = new WebSettingsStore(settingsStore);
            using var viewer = new ViewerService(store, configuration);
            var snapshot = JsonSerializer.SerializeToElement(viewer.Snapshot());
            var settings = snapshot.GetProperty("settings");
            var playback = snapshot.GetProperty("playback");
            Check(playback.GetProperty("retryDelaysSeconds").GetString() == settings.GetProperty("RetryDelaysSeconds").GetString() &&
                playback.GetProperty("acknowledgementTimeoutSeconds").GetDouble() == CameraVideoEndpoints.AcknowledgementTimeout.TotalSeconds,
                "Browser recovery receives the active feed schedule and the actual server acknowledgement deadline");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ScheduleValidation()
    {
        var legacy = JsonSerializer.Deserialize<ViewerSettings>("{}")!;
        Check(legacy.RetryDelaysSeconds == CameraRetrySchedule.DefaultDelaysSeconds && legacy.Validate() is null,
            "Existing configurations receive the default retry schedule");
        var explicitCameras = legacy.WithSources();
        foreach (var invalid in new string?[] { null, "", " ", "0", "-1", "1.5", "1,,2", ",1", "1,", "abc", "1;2", "2147483648", "+1", "1e2" })
        {
            Check(!CameraRetrySchedule.TryParse(invalid, out _) &&
                (legacy with { RetryDelaysSeconds = invalid! }).Validate() is not null &&
                (explicitCameras with { RetryDelaysSeconds = invalid! }).Validate() is not null,
                $"Invalid retry schedule is rejected for templates and explicit cameras: {invalid ?? "null"}");
        }

        var schedule = CameraRetrySchedule.Parse(" 2, 7 , 11 ");
        Check(schedule.GetDelay(0).TotalSeconds == 2 && schedule.GetDelay(1).TotalSeconds == 7 &&
            schedule.GetDelay(2).TotalSeconds == 11 && schedule.GetDelay(int.MaxValue).TotalSeconds == 11,
            "Custom schedules tolerate surrounding whitespace and repeat the final delay indefinitely");
        Check(CameraRetrySchedule.Parse("9").GetDelay(100).TotalSeconds == 9 &&
            CameraRetrySchedule.Parse("2147483647").GetDelay(0).TotalSeconds == int.MaxValue &&
            CameraRetrySchedule.Parse(string.Join(',', Enumerable.Repeat(1, 10000))).GetDelay(9999).TotalSeconds == 1,
            "Single entries, long delays and long schedules have no arbitrary feature limits");
        Throws<ArgumentException>(() => CameraRetrySchedule.Parse("bad"), "Parsing an invalid schedule fails clearly");
        Throws<ArgumentOutOfRangeException>(() => schedule.GetDelay(-1), "Negative failure indexes are rejected");
    }

    private static void RecoveryDecisions()
    {
        var recovery = new CameraRecovery();
        var now = TimeSpan.Zero;
        int[] expected = [1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 8, 8, 16, 32, 32, 32];
        foreach (var seconds in expected)
        {
            recovery.StartAttempt(now);
            recovery.Failed(now);
            var retryAt = now + TimeSpan.FromSeconds(seconds);
            Check(recovery.RetryAt == retryAt && !recovery.ShouldRetry(retryAt - TimeSpan.FromTicks(1)) && recovery.ShouldRetry(retryAt),
                $"Default retry delay and exact deadline are correct: {seconds}s");
            recovery.Failed(now + TimeSpan.FromMilliseconds(100));
            Check(recovery.RetryAt == retryAt, "Duplicate failures do not advance or extend an existing retry");
            now = retryAt;
        }

        recovery = new("2, 7, 11");
        recovery.StartAttempt(TimeSpan.Zero);
        recovery.Failed(TimeSpan.Zero);
        recovery.StartAttempt(TimeSpan.FromSeconds(2));
        recovery.FrameArrived(TimeSpan.FromSeconds(3));
        recovery.Failed(TimeSpan.FromSeconds(4));
        Check(recovery.RetryAt == TimeSpan.FromSeconds(11), "A brief return of video does not reset the failure sequence");
        recovery.FrameArrived(TimeSpan.FromSeconds(5));
        Check(recovery.RetryAt is null && !recovery.ShouldRetry(TimeSpan.FromSeconds(11)), "Resumed video cancels a pending retry");
        recovery.FrameArrived(TimeSpan.FromSeconds(34));
        recovery.Failed(TimeSpan.FromSeconds(34));
        Check(recovery.RetryAt == TimeSpan.FromSeconds(45), "Less than 30 healthy seconds does not reset the sequence");
        recovery.StartAttempt(TimeSpan.FromSeconds(45));
        recovery.FrameArrived(TimeSpan.FromSeconds(46));
        recovery.FrameArrived(TimeSpan.FromSeconds(76));
        recovery.Failed(TimeSpan.FromSeconds(77));
        Check(recovery.RetryAt == TimeSpan.FromSeconds(79), "30 healthy seconds reset the sequence to its first delay");
        recovery.StartAttempt(TimeSpan.FromSeconds(79));
        Check(recovery.IsStale(TimeSpan.FromSeconds(79)) && !recovery.IsStalled(TimeSpan.FromSeconds(108)) &&
            recovery.IsStalled(TimeSpan.FromSeconds(109)), "Configuring retry delays preserves the existing stall watchdog");
    }

    private static async Task RetryCancellationAsync()
    {
        var recovery = new CameraRecovery("2147483647");
        await recovery.WaitForRetryAsync(() => TimeSpan.Zero, CancellationToken.None);
        recovery.Failed(TimeSpan.Zero);
        using var stop = new CancellationTokenSource();
        var waiting = recovery.WaitForRetryAsync(() => TimeSpan.Zero, stop.Token);
        Check(!waiting.IsCompleted, "A delay beyond the platform timer range waits without overflowing");
        await stop.CancelAsync();
        try
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("FAIL: Retry cancellation was ignored");
        }
        catch (OperationCanceledException)
        {
            Check(true, "Cancellation promptly interrupts a long retry delay");
        }
        await recovery.WaitForRetryAsync(() => TimeSpan.FromSeconds(int.MaxValue), CancellationToken.None);
    }

    private static async Task PersistenceAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "camera-retry-tests", Guid.NewGuid().ToString("N")));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "settings.json")
        }).Build();
        var store = new WebSettingsStore(new JsonAppSettingsStore(configuration));
        var settings = new ViewerSettings
        {
            AutoConnect = false,
            RetryDelaysSeconds = "3, 6, 12",
            Sources = [new() { Channel = 1, MainStream = "rtsp://127.0.0.1/live", Enabled = false }]
        };
        var other = settings with
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            ConfigurationName = "Other cameras",
            RetryDelaysSeconds = "9"
        };
        using (var viewer = new ViewerService(store, configuration))
        {
            await viewer.ConfigureAsync(new(settings, null));
            await viewer.ConfigureAsync(new(other, null));
            await viewer.ActivateAsync(settings.ConfigurationId);
            try
            {
                await viewer.ConfigureAsync(new(settings with
                {
                    RetryDelaysSeconds = "1,,2"
                }, null));
                throw new Exception("FAIL: Invalid configuration was saved");
            }
            catch (ArgumentException)
            {
                Check(store.Load(false).Settings.RetryDelaysSeconds == "3, 6, 12", "Invalid saves preserve the active retry schedule");
            }
        }
        using (var restarted = new ViewerService(new WebSettingsStore(new JsonAppSettingsStore(configuration)), configuration))
        {
            var catalog = JsonSerializer.SerializeToElement(restarted.Configurations());
            Check(catalog.GetProperty("defaultRetryDelaysSeconds").GetString() == CameraRetrySchedule.DefaultDelaysSeconds,
                "The restore-default button receives its schedule from the shared server definition");
            var snapshot = JsonSerializer.SerializeToElement(restarted.Snapshot());
            Check(snapshot.GetProperty("settings").GetProperty("RetryDelaysSeconds").GetString() == "3, 6, 12" &&
                store.LoadCatalog().Configurations.Single(value => value.ConfigurationId == other.ConfigurationId).RetryDelaysSeconds == "9",
                "Retry schedules persist across restart and remain independent for each configuration");
            Check(settings.Normalize().WithSources().RetryDelaysSeconds == "3, 6, 12" &&
                JsonSerializer.Deserialize<ViewerSettings>(JsonSerializer.Serialize(settings))!.RetryDelaysSeconds == "3, 6, 12",
                "Portable configuration and source conversion preserve custom retry schedules");
        }
    }

    private static async Task PlaybackRetriesAsync()
    {
        var settings = new ViewerSettings { RetryDelaysSeconds = "2, 1, 32" };
        var router = new FailedRouter();
        await using (var camera = new WebCamera(settings, "", 1, "unused-ffmpeg", 0, connectionRouter: router))
        {
            await UntilAsync(() => router.Attempts.Count >= 3);
            CheckIntervals(router.Attempts, "JPEG preview");
            Check(camera.State.Status == "retrying", "JPEG retries expose the connection failure state");
            await camera.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
        var source = new FailedVideoSource();
        var hub = new LiveMediaHub(source);
        await using var subscription = hub.Subscribe(settings, "", 1, null);
        await UntilAsync(() => source.Attempts.Count >= 3);
        CheckIntervals(source.Attempts, "Encoded video");
        await hub.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var count = source.Attempts.Count;
        await Task.Delay(100);
        Check(source.Attempts.Count == count && hub.SessionCount == 0, "Stopping playback cancels retries during the final repeated delay");
    }

    private static void CheckIntervals(ConcurrentQueue<long> attempts, string method)
    {
        var times = attempts.ToArray();
        Check(Stopwatch.GetElapsedTime(times[0], times[1]).TotalSeconds >= 1.9 &&
            Stopwatch.GetElapsedTime(times[1], times[2]).TotalSeconds >= 0.9,
            $"{method} applies the configured first and second retry delays");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
            throw new Exception("FAIL: " + message);
        }
        catch (T)
        {
            Check(true, message);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        Console.WriteLine("PASS: " + message);
    }

    private sealed class FailedRouter : ICameraConnectionRouter
    {
        public ConcurrentQueue<long> Attempts { get; } = new();

        public CameraConnection Resolve(ViewerSettings settings, string password, int channel)
        {
            Attempts.Enqueue(Stopwatch.GetTimestamp());
            throw new IOException("Connection lost");
        }
    }

    private sealed class FailedVideoSource : ILiveMediaSource
    {
        public ConcurrentQueue<long> Attempts { get; } = new();

        public Task RunAsync(ViewerSettings settings, string password, int channel,
            Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            Attempts.Enqueue(Stopwatch.GetTimestamp());
            return Task.FromException(new IOException("Connection lost"));
        }
    }
}
