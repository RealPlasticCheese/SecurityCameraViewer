using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecurityCameraViewer.Recording;

internal static class RecordingArchiveTests
{
    public static async Task RunAsync(string directory, FfmpegProcess ffmpeg, byte[] initialization, VideoFragment[] fragments)
    {
        var settings = new RecordingSettings
        {
            ConfigurationId = "workshop",
            Directory = Path.Combine(directory, "shared-archive"),
            Cameras = [new() { Channel = 1, CameraId = "camera-stable-id", Mode = RecordingModes.Continuous }]
        };
        var store = new FileRecordingStore(settings, ffmpeg);
        Check(!Directory.Exists(settings.Directory) && store.Count == 0,
            "Opening an empty archive does not create files or a SQLite database");
        var start = DateTimeOffset.UtcNow.AddMinutes(-2);
        string id;
        await using (var writer = await store.BeginTriggeredAsync(1, "Workshop", initialization, 0,
            new RecordingTrigger("continuous", "Continuous recording") with
            {
                Timestamp = start
            }, CancellationToken.None, start))
        {
            id = writer.Id!;
            var catalog = new SqliteRecordingCatalog(settings.Directory, settings.ConfigurationId);
            var before = File.GetLastWriteTimeUtc(Path.Combine(settings.Directory, "archive.db"));
            foreach (var fragment in fragments.Take(3))
            {
                await writer.AppendAsync(fragment, CancellationToken.None);
            }
            Check(File.GetLastWriteTimeUtc(Path.Combine(settings.Directory, "archive.db")) == before &&
                catalog.Find(id)!.SizeBytes < store.Find(id)!.SizeBytes,
                "Appending video updates active accounting without per-fragment database writes");
            var zone = new EventZone("entrance", "Entrance");
            for (var index = 0; index < 150; index++)
            {
                await writer.AddTriggerAsync(new RecordingTrigger("motion", "Motion", Zones: [zone]) with
                {
                    Timestamp = start.AddMilliseconds(index)
                }, CancellationToken.None);
            }
            await writer.AddTriggerAsync(new RecordingTrigger("sensor", "Contact opened", "archive-request") with
            {
                Timestamp = start.AddSeconds(1)
            }, CancellationToken.None);
            await writer.CompleteAsync(false, CancellationToken.None);
        }
        await store.MaintainAsync(CancellationToken.None);
        var recording = store.Find(id)!;
        Check(recording.Mode == RecordingModes.Continuous && recording.StartedAt == start && recording.Status == "ready",
            "Continuous recordings retain their true first-fragment time, mode and finalized status");
        var expectedFolder = Path.Combine(settings.Directory, settings.ConfigurationId, "camera-stable-id",
            start.UtcDateTime.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture));
        Check(Path.GetDirectoryName(store.MediaPath(id)) == Path.GetFullPath(expectedFolder) &&
            !Directory.EnumerateFiles(settings.Directory, "*.json", SearchOption.AllDirectories).Any(),
            "All new modes use the configuration/camera/UTC-date media layout and SQLite metadata");
        Check(store.Markers(id).Length == 152 && store.FindByRequest("archive-request").Single().Id == id,
            "Repeated motion and external markers persist beyond the bounded compatibility trigger summary");
        Check(store.Search(new()
        {
            Zones = [new(1, "entrance")]
        }).Single().Id == id,
            "Indexed zone queries find footage associated with activity markers");
        Check(store.Search(new()
        {
            Zones = [new(1, "entrance"), .. Enumerable.Range(1, 1023).Select(index => new RecordingZoneSelection(1, "zone-" + index))]
        }).Single().Id == id,
            "Zone queries support the full 1024-zone limit without expression depth errors");
        await store.ProtectAsync(id, true, CancellationToken.None);
        var restarted = new FileRecordingStore(settings, ffmpeg);
        Check(restarted.Find(id)!.Protected && restarted.Markers(id).Length == 152 && restarted.UsedBytes == store.UsedBytes,
            "Archive size, protection and all markers survive restart");
        Check(restarted.Timeline(1, start.AddSeconds(1), start.AddMinutes(1)).Recordings.Single().Id == id,
            "Timeline queries include a recording that starts before the selected range");
        Check(restarted.TimelineByCameraId("camera-stable-id", start.AddSeconds(1), start.AddMinutes(1)).Recordings.Single().Id == id,
            "Integration timeline queries use stable camera identity");
        var otherConfiguration = new FileRecordingStore(settings with
        {
            ConfigurationId = "other"
        }, ffmpeg);
        Check(otherConfiguration.Count == 0 && otherConfiguration.Find(id) is null && otherConfiguration.Markers(id).Length == 0,
            "Configurations sharing a root have isolated libraries and marker queries");

        var layout = new RecordingFileLayout(settings.Directory);
        Check(!recording.FileBasePath.Contains('\\') &&
            layout.PathFor(recording with
            {
                FileBasePath = recording.FileBasePath.Replace('/', '\\')
            }, ".mp4") == store.MediaPath(id),
            "Archive paths are portable between Windows and Linux directory separators");
        try
        {
            layout.PathFor(recording with
            {
                FileBasePath = "../outside"
            }, ".mp4");
            throw new Exception("Unsafe path was accepted");
        }
        catch (IOException)
        {
            Check(true, "Catalogue media paths cannot escape the selected recording root");
        }

        var legacyDirectory = Path.Combine(directory, "legacy-archive", "events");
        Directory.CreateDirectory(legacyDirectory);
        var legacy = recording with
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = RecordingModes.Event,
            FileBasePath = "",
            Protected = true
        };
        var legacyPath = Path.Combine(legacyDirectory, legacy.Id + ".mp4");
        File.Copy(store.MediaPath(id)!, legacyPath);
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, legacy.Id + ".json"), JsonSerializer.Serialize(legacy));
        var migrated = new FileRecordingStore(settings with
        {
            Directory = Path.GetDirectoryName(legacyDirectory)!
        }, ffmpeg);
        Check(migrated.MediaPath(legacy.Id) == legacyPath && migrated.Find(legacy.Id)!.Protected,
            "Legacy JSON metadata imports without moving footage or losing protection");
        var migratedAgain = new FileRecordingStore(settings with
        {
            Directory = Path.GetDirectoryName(legacyDirectory)!
        }, ffmpeg);
        Check(migratedAgain.Count == 1, "Legacy migration is idempotent");
        var otherLegacy = legacy with
        {
            Id = Guid.NewGuid().ToString("N"),
            ConfigurationId = "other"
        };
        File.Copy(legacyPath, Path.Combine(legacyDirectory, otherLegacy.Id + ".mp4"));
        await File.WriteAllTextAsync(Path.Combine(legacyDirectory, otherLegacy.Id + ".json"), JsonSerializer.Serialize(otherLegacy));
        var otherLegacySettings = settings with
        {
            Directory = Path.GetDirectoryName(legacyDirectory)!,
            ConfigurationId = "other"
        };
        var migratedOther = new FileRecordingStore(otherLegacySettings, ffmpeg);
        Check(migratedOther.Count == 1 && migratedOther.Find(legacy.Id) is null && migratedOther.Find(otherLegacy.Id)!.Protected,
            "Legacy migration honors explicit configuration ownership in a shared recording root");
        await migratedOther.ProtectAsync(otherLegacy.Id, false, CancellationToken.None);
        _ = new FileRecordingStore(settings with
        {
            Directory = Path.GetDirectoryName(legacyDirectory)!
        }, ffmpeg);
        Check(!new FileRecordingStore(otherLegacySettings, ffmpeg).Find(otherLegacy.Id)!.Protected,
            "Repeated legacy imports never overwrite another configuration's updated protection");

        var interrupted = recording with
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "recording",
            DurationSeconds = 0,
            EndedAt = null,
            SizeBytes = initialization.Length,
            FileBasePath = "",
            Protected = false
        };
        interrupted = interrupted with
        {
            FileBasePath = layout.CreateBasePath(interrupted)
        };
        var capturePath = layout.PathFor(interrupted, ".capture.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(capturePath)!);
        await using (var capture = File.Create(capturePath))
        {
            await capture.WriteAsync(initialization);
            foreach (var fragment in fragments.Take(3))
            {
                await capture.WriteAsync(fragment.Bytes);
            }
        }
        new SqliteRecordingCatalog(settings.Directory, settings.ConfigurationId).Save(interrupted);
        var recovered = new FileRecordingStore(settings, ffmpeg);
        Check(recovered.Find(interrupted.Id)!.Status == "finalizing" && recovered.Find(interrupted.Id)!.Interrupted,
            "An unfinished capture in the new layout is recovered after an abrupt restart");
        await recovered.MaintainAsync(CancellationToken.None);
        Check(recovered.Find(interrupted.Id)!.Status == "ready" && recovered.Find(interrupted.Id)!.DurationSeconds > 0 && !File.Exists(capturePath),
            "Crash recovery reconstructs duration from complete fragments and finalizes playable footage");

        var futureDirectory = Path.Combine(directory, "future-archive");
        Directory.CreateDirectory(futureDirectory);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(futureDirectory, "archive.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=2";
            command.ExecuteNonQuery();
        }
        try
        {
            _ = new FileRecordingStore(settings with
            {
                Directory = futureDirectory
            }, ffmpeg);
            throw new Exception("Future database was accepted");
        }
        catch (IOException)
        {
            Check(true, "Unsupported future catalogues fail closed without replacing the database");
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
}
