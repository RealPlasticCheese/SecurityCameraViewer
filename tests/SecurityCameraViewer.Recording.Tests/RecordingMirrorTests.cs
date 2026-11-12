using System.Diagnostics;
using System.Text.Json;
using SecurityCameraViewer.Recording;

internal static class RecordingMirrorTests
{
    private const double SmallQuota = 0.00000009;
    private const double LargeQuota = 0.000001;
    private const string MarkerFile = ".recording-location-id";

    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "recording-mirrors-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var ffmpeg = new FfmpegProcess("missing-test-ffmpeg");
        var primaryPath = Path.Combine(root, "primary");
        var smallPath = Path.Combine(root, "small");
        var largePath = Path.Combine(root, "large");
        var unplugged = Path.Combine(root, "unplugged");
        var originalSettings = new RecordingSettings
        {
            Directory = primaryPath, MaximumStorageGigabytes = LargeQuota, RetentionDays = 0,
            Cameras = [new() { Channel = 1, Mode = RecordingModes.Continuous }]
        };
        var media = Path.Combine(root, "video.mp4");
        await File.WriteAllBytesAsync(media, Enumerable.Repeat((byte)5, 40).ToArray());
        await File.WriteAllBytesAsync(media + ".jpg", [7, 8]);
        RecordingStoragePool? pool = null;
        try
        {
            var original = new FileRecordingStore(originalSettings, ffmpeg);
            var now = DateTimeOffset.UtcNow;
            var old = await Import(original, media, now.AddMinutes(-3));
            var protectedClip = await Import(original, media, now.AddMinutes(-2));
            var latest = await Import(original, media, now.AddMinutes(-1));
            await original.ProtectAsync(protectedClip.Id, true, CancellationToken.None);
            var zone = new EventZone("door", "Front door");
            var markers = Enumerable.Range(0, 8200).Select(_ => new RecordingTrigger(Zones: [zone])
                { Timestamp = latest.StartedAt.AddSeconds(1) }).ToArray();
            var sourceCatalogue = new SqliteRecordingCatalog(primaryPath, originalSettings.ConfigurationId);
            sourceCatalogue.Save(latest, markers);

            Directory.CreateDirectory(smallPath);
            Directory.CreateDirectory(largePath);
            var primary = new RecordingLocation(primaryPath, LargeQuota).Register(originalSettings.ConfigurationId);
            var small = new RecordingLocation(smallPath, SmallQuota).Register();
            var large = new RecordingLocation(largePath, LargeQuota).Register();
            var settings = originalSettings with { PrimaryLocationId = primary.Id, Mirrors = [large, small] };
            var interruptedLayout = new RecordingFileLayout(largePath, "archive-" + large.Id);
            var incomplete = latest with { FileBasePath = interruptedLayout.CreateBasePath(latest) };
            var stagingFile = interruptedLayout.PathFor(incomplete, ".mp4.mirror.tmp");
            await File.WriteAllBytesAsync(stagingFile, [0]);
            Check(settings.Validate() is null && (settings with { Mirrors = [small, small] }).Validate() is not null &&
                (settings with { Mirrors = [small with { Directory = Path.Combine(primaryPath, "child") }] }).Validate() is not null &&
                (settings with { Mirrors = [small with { MaximumStorageGigabytes = double.NaN }] }).Validate() is not null,
                "Settings accept mixed quotas and reject duplicate, nested and invalid destinations");
            Check(File.ReadAllText(Path.Combine(primaryPath, MarkerFile)) == primary.Id &&
                primary.Id != small.Id && small.Id != large.Id,
                "Each destination has its own portable folder identity");

            var stateFile = Path.Combine(root, "active.json");
            pool = Start(settings, new());
            await Until(pool, () => pool.Snapshot().Count(location => location.CanActivate) == 2,
                "Both registered mirrors catch up independently");
            Check(pool.ActiveStore!.Find(old.Id) is not null && File.Exists(original.MediaPath(old.Id)),
                "Registering the legacy primary preserves its catalogue and original media path");

            var smallLibrary = Open(small, settings);
            var largeLibrary = Open(large, settings);
            Check(smallLibrary.Find(old.Id) is null && smallLibrary.Find(protectedClip.Id)?.Protected == true &&
                smallLibrary.Find(latest.Id) is not null && largeLibrary.Find(old.Id) is not null,
                "The smaller mirror keeps the newest footage and protected clips without pruning another location");
            var copiedCatalogue = new SqliteRecordingCatalog(Path.Combine(largePath, "archive-" + large.Id), settings.ConfigurationId);
            Check(copiedCatalogue.Markers(latest.Id, all: true).Count(marker => marker.Zones?.Contains(zone) == true) == markers.Length &&
                largeLibrary.Search(new() { Zones = [new(1, zone.Id)] }).Single().Id == latest.Id,
                "Mirrors preserve all motion markers beyond the display limit and index their zones");
            Check(!File.Exists(stagingFile) && File.ReadAllBytes(largeLibrary.MediaPath(latest.Id)!).SequenceEqual(File.ReadAllBytes(media)) &&
                File.ReadAllBytes(largeLibrary.MediaPath(latest.Id, thumbnail: true)!).SequenceEqual([ (byte)7, (byte)8 ]),
                "An interrupted copy is replaced and only complete video and thumbnails are published");

            var sourceProtection = pool.ActiveStore!;
            await sourceProtection.ProtectAsync(latest.Id, true, CancellationToken.None);
            await Until(pool, () => Open(large, settings).Find(latest.Id)?.Protected == true,
                "Protection changes are replicated to an already copied clip");

            Directory.Move(primaryPath, unplugged);
            Directory.CreateDirectory(primaryPath);
            await Until(pool, () => pool.Snapshot().Any(location => location.Active && location.Directory != primaryPath),
                "The server automatically promotes a healthy mirror when the primary vanishes");
            var active = pool.State.ActiveDirectory!;
            Check(Directory.GetFileSystemEntries(primaryPath).Length == 0 &&
                pool.State.PreviousDirectory == primaryPath && pool.State.LastFailoverAt is not null,
                "The empty mount point remains empty and failover records its recovery point");

            await using (var capture = await pool.BeginAsync(1, "Camera 1", [1, 2], 0, CancellationToken.None))
            {
                Check(pool.ActiveStore!.Find(capture.Id!)?.Status == "recording",
                    "The normal capture entry point resumes on the promoted drive");
            }

            await pool.ActiveStore!.ProtectAsync(latest.Id, false, CancellationToken.None);
            var afterFailover = await Import(pool.ActiveStore!, media, now.AddMinutes(1));
            Directory.Delete(primaryPath);
            Directory.Move(unplugged, primaryPath);
            await Until(pool, () => pool.Snapshot().Any(location => location.Directory == primaryPath && location.CanActivate),
                "The original drive catches up after reconnecting");
            Check(pool.State.ActiveDirectory == active && Open(primary, settings).Find(afterFailover.Id) is not null,
                "The old primary returns as a mirror and receives new footage");

            await pool.StopAsync();
            var persisted = JsonSerializer.Deserialize<RecordingStorageState>(await File.ReadAllTextAsync(stateFile))!;
            pool = Start(settings, persisted);
            await Until(pool, () => pool.ActiveStore is not null && pool.Snapshot().Any(location => location.Directory == primaryPath && location.CanActivate),
                "The promoted drive remains active after server restart");
            Check(pool.State.ActiveDirectory == active, "The original primary stays secondary across restart");
            pool.Activate(primaryPath);
            Check(pool.State.ActiveDirectory == primaryPath && pool.ActiveStore!.Find(afterFailover.Id) is not null,
                "An administrator can switch back after catch-up");

            var markerPath = Path.Combine(primaryPath, MarkerFile);
            await File.WriteAllTextAsync(markerPath, Guid.NewGuid().ToString("N"));
            await Until(pool, () => pool.Snapshot().Any(location => location.Directory == primaryPath && location.Status == "wrong-folder"),
                "A wrong identity at the same path is rejected");
            var registered = await File.ReadAllTextAsync(markerPath);
            Check(registered != primary.Id && pool.State.ActiveDirectory != primaryPath, "Runtime checks leave a replacement identity untouched");

            File.Delete(Path.Combine(smallPath, MarkerFile));
            File.Delete(Path.Combine(largePath, MarkerFile));
            await Until(pool, () => pool.ActiveStore is null, "All unavailable drives suspend capture without recreating identity files");
            await File.WriteAllTextAsync(Path.Combine(largePath, MarkerFile), large.Id);
            await Until(pool, () => pool.ActiveStore is not null && pool.State.ActiveDirectory == largePath,
                "Recording automatically resumes when a usable drive returns");

            await pool.StopAsync();
            await File.WriteAllTextAsync(markerPath, primary.Id);
            var reduced = settings with { Directory = largePath, PrimaryLocationId = large.Id,
                MaximumStorageGigabytes = 0.000000001, Mirrors = [primary] };
            pool = Start(reduced, new(largePath));
            await Until(pool, () => pool.State.ActiveDirectory == primaryPath &&
                pool.Snapshot().Any(location => location.Directory == largePath && location.Status == "full"),
                "A reduced quota with protected footage triggers failover to a drive with space");
            Check(Open(large, settings).Find(protectedClip.Id)?.Protected == true &&
                Open(primary, settings).Find(old.Id) is not null,
                "Lowering one quota preserves protected footage and leaves other drives' older footage intact");

            RecordingStoragePool Start(RecordingSettings config, RecordingStorageState state) =>
                new(config, ffmpeg, NullCameraEventPublisher.Instance, TimeSpan.FromSeconds(2), state,
                    value => File.WriteAllText(stateFile, JsonSerializer.Serialize(value)));

            FileRecordingStore Open(RecordingLocation location, RecordingSettings config) =>
                new(config with { Directory = location.Directory, MaximumStorageGigabytes = location.MaximumStorageGigabytes },
                    ffmpeg, location: location);
        }
        finally
        {
            if (pool is not null)
            {
                await pool.StopAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<RecordingEvent> Import(FileRecordingStore store, string media, DateTimeOffset start)
    {
        var clip = new RecordingEvent
        {
            Channel = 1, StartedAt = start, TriggeredAt = start, EndedAt = start.AddSeconds(4),
            DurationSeconds = 4, Mode = RecordingModes.Continuous, Trigger = new("continuous", "Continuous recording")
        };
        return (await store.ImportAsync([new(clip, media)], CancellationToken.None)).Single();
    }

    private static async Task Until(RecordingStoragePool pool, Func<bool> ready, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            pool.Poll();
            if (elapsed.Elapsed > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException(description + ": " + JsonSerializer.Serialize(pool.Snapshot()));
            }
            await Task.Delay(30);
        }
        Check(true, description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine("PASS: " + description);
    }
}
