using SecurityCameraViewer.Recording;

internal static class RecordingRetentionTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "scv-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new RecordingSettings { Directory = root, RetentionDays = 0 };
            Check(settings.Validate() is null && (settings with { RetentionDays = -1 }).Validate() is not null &&
                (settings with { RetentionDays = 366 }).Validate() is not null,
                "Retention validation accepts zero and rejects values outside 0–365 days");

            var layout = new RecordingFileLayout(root);
            var catalog = new SqliteRecordingCatalog(root, settings.ConfigurationId);
            catalog.Initialize();
            var old = Seed(new DateTimeOffset(2000, 10, 9, 0, 0, 0, TimeSpan.Zero), "ready");
            var failed = Seed(old.StartedAt.AddDays(1), "failed");
            var protectedClip = Seed(old.StartedAt.AddYears(-1), "ready", isProtected: true);
            var camera = Path.Combine(root, settings.ConfigurationId, "camera-2");
            var historical = Path.Combine(camera, "2001", "10", "09");
            Directory.CreateDirectory(historical);
            var unrelated = Path.Combine(camera, "2002", "notes");
            Directory.CreateDirectory(unrelated);
            var foreign = Path.Combine(root, "other", "camera-2", "2001", "10", "09");
            Directory.CreateDirectory(foreign);
            var sidecar = Path.Combine(Day(failed), ".keep");
            File.WriteAllText(sidecar, "unmanaged");

            var ffmpeg = new FfmpegProcess("ffmpeg");
            var store = new FileRecordingStore(settings, ffmpeg);
            await store.MaintainAsync(CancellationToken.None);
            Check(store.Count == 3, "Zero retention preserves old ready and failed recordings");
            Check(!Directory.Exists(Path.Combine(camera, "2001")) && Directory.Exists(unrelated) &&
                Directory.Exists(foreign), "Maintenance prunes existing empty date trees within the active configuration");

            var partialQuota = new FileRecordingStore(settings with { MaximumStorageGigabytes = 1.2 }, ffmpeg);
            await partialQuota.MaintainAsync(CancellationToken.None);
            Check(partialQuota.Find(old.Id) is null && partialQuota.Find(failed.Id) is not null,
                "Quota evicts the oldest unprotected recording first");

            var quota = new FileRecordingStore(settings with { MaximumStorageGigabytes = 1 }, ffmpeg);
            await quota.MaintainAsync(CancellationToken.None);
            Check(quota.Find(old.Id) is null && quota.Find(failed.Id) is null &&
                quota.Find(protectedClip.Id)?.Protected == true, "Zero retention still evicts oldest unprotected recordings for quota");
            Check(!Directory.Exists(Day(old)) && File.Exists(sidecar) && Directory.Exists(Day(protectedClip)),
                "Pruning preserves directories with unmanaged files and protected media");

            var manual = Seed(old.StartedAt.AddYears(3), "ready");
            var manualStore = new FileRecordingStore(settings, ffmpeg);
            Check(await manualStore.DeleteAsync(manual.Id, CancellationToken.None) &&
                !Directory.Exists(Path.Combine(camera, "2003")) && Directory.Exists(camera),
                "Manual deletion prunes empty day, month and year directories up to the camera");

            var expires = Seed(old.StartedAt.AddYears(4), "failed");
            var ageLimit = new FileRecordingStore(settings with { RetentionDays = 7 }, ffmpeg);
            await ageLimit.MaintainAsync(CancellationToken.None);
            Check(ageLimit.Find(expires.Id) is null && ageLimit.Find(protectedClip.Id) is not null,
                "Positive retention still expires old unprotected recordings");

            var now = DateTimeOffset.UtcNow;
            var sameDay = Seed(now, "ready");
            var activeStore = new FileRecordingStore(settings, ffmpeg);
            await using var writer = await activeStore.BeginTriggeredAsync(2, "Camera 2", [1], 0,
                new RecordingTrigger("motion", "Motion"), CancellationToken.None, now);
            await Task.WhenAll(activeStore.MaintainAsync(CancellationToken.None),
                activeStore.DeleteAsync(sameDay.Id, CancellationToken.None));
            Check(Directory.Exists(Day(sameDay)) && activeStore.Find(writer.Id!)?.Status == "recording",
                "Maintenance and deletion preserve the directory shared with an active capture");

            RecordingEvent Seed(DateTimeOffset date, string status, bool isProtected = false)
            {
                var item = new RecordingEvent
                {
                    Channel = 2,
                    StartedAt = date,
                    TriggeredAt = date,
                    SizeBytes = 550_000_000,
                    Status = status,
                    Protected = isProtected
                };
                item = item with { FileBasePath = layout.CreateBasePath(item) };
                Directory.CreateDirectory(Day(item));
                File.WriteAllBytes(layout.PathFor(item, ".mp4"), [1]);
                catalog.Save(item);
                return item;
            }

            string Day(RecordingEvent item) => Path.GetDirectoryName(layout.PathFor(item, ".mp4"))!;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }

        Console.WriteLine("PASS: " + message);
    }
}
