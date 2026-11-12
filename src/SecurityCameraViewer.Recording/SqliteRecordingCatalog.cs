using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SecurityCameraViewer.Recording;

/// <summary>SQLite catalogue scoped by configuration, with short-lived connections and transactional metadata updates.</summary>
public sealed class SqliteRecordingCatalog(string directory, string configurationId, bool createDirectory = true) : IRecordingCatalog
{
    private readonly string _databasePath = Path.Combine(Path.GetFullPath(directory), "archive.db");
    public bool Exists => File.Exists(_databasePath);

    public void Initialize() => Execute(() =>
    {
        if (createDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! > 1)
        {
            throw new IOException("This recording catalogue was created by a newer server version.");
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Recordings (
                Id TEXT PRIMARY KEY, ConfigurationId TEXT NOT NULL, CameraId TEXT NOT NULL,
                Channel INTEGER NOT NULL, CameraName TEXT NOT NULL, StartedAt INTEGER NOT NULL,
                TriggeredAt INTEGER NOT NULL, EndedAt INTEGER, Status TEXT NOT NULL,
                Protected INTEGER NOT NULL, SizeBytes INTEGER NOT NULL,
                FileBasePath TEXT NOT NULL, Metadata TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Recordings_Time ON Recordings(ConfigurationId, TriggeredAt DESC, Id);
            CREATE INDEX IF NOT EXISTS IX_Recordings_CameraTime ON Recordings(ConfigurationId, CameraId, TriggeredAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Recordings_ChannelTime ON Recordings(ConfigurationId, Channel, TriggeredAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Recordings_Retention ON Recordings(ConfigurationId, Status, Protected, TriggeredAt);
            CREATE INDEX IF NOT EXISTS IX_Recordings_Coverage ON Recordings(ConfigurationId, Channel, StartedAt);
            CREATE INDEX IF NOT EXISTS IX_Recordings_CameraCoverage ON Recordings(ConfigurationId, CameraId, StartedAt);
            CREATE INDEX IF NOT EXISTS IX_Recordings_ReplayTime ON Recordings(ConfigurationId,
                json_extract(Metadata, '$.Trigger.Source'), TriggeredAt DESC);
            CREATE TABLE IF NOT EXISTS RecordingMarkers (
                RecordingId TEXT NOT NULL REFERENCES Recordings(Id) ON DELETE CASCADE,
                Id TEXT NOT NULL, Timestamp INTEGER NOT NULL, RequestId TEXT, Metadata TEXT NOT NULL,
                PRIMARY KEY(RecordingId, Id));
            CREATE INDEX IF NOT EXISTS IX_RecordingMarkers_Request ON RecordingMarkers(RequestId, RecordingId);
            CREATE TABLE IF NOT EXISTS RecordingZones (
                RecordingId TEXT NOT NULL REFERENCES Recordings(Id) ON DELETE CASCADE,
                ZoneId TEXT NOT NULL, Name TEXT NOT NULL, PRIMARY KEY(RecordingId, ZoneId));
            CREATE INDEX IF NOT EXISTS IX_RecordingZones_Zone ON RecordingZones(ZoneId, RecordingId);
            CREATE TABLE IF NOT EXISTS RecordingPruned (
                ConfigurationId TEXT NOT NULL, Id TEXT NOT NULL, PRIMARY KEY(ConfigurationId, Id));
            CREATE TABLE IF NOT EXISTS RecordingLocationMigrations (ConfigurationId TEXT PRIMARY KEY);
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
    });

    public RecordingEvent? Find(string id) => Read(command =>
    {
        command.CommandText = "SELECT Metadata, FileBasePath FROM Recordings WHERE ConfigurationId=$configuration AND Id=$id";
        command.Parameters.AddWithValue("$id", id);
        return Rows(command).SingleOrDefault();
    });

    public RecordingEvent[] Search(RecordingSearch search, string? cameraId = null, string? status = null, bool oldestFirst = false, bool? protectedOnly = null) => Read(command =>
    {
        var conditions = new List<string> { "ConfigurationId=$configuration" };
        Add("Channel", search.Channel);
        Add("CameraId", cameraId);
        Add("Status", status);
        Add("Protected", protectedOnly);
        if (search.SavedReplayOnly)
        {
            conditions.Add("json_extract(r.Metadata, '$.Trigger.Source') = 'replay'");
        }
        if (search.Since is { } since)
        {
            conditions.Add("TriggeredAt >= $since");
            command.Parameters.AddWithValue("$since", since.UtcTicks);
        }
        if (search.Before is { } before)
        {
            conditions.Add("TriggeredAt < $before");
            command.Parameters.AddWithValue("$before", before.UtcTicks);
        }
        if (search.Zones.Length > 0)
        {
            var zones = new List<string>();
            for (var index = 0; index < search.Zones.Length; index++)
            {
                zones.Add($"($channel{index},$zone{index})");
                command.Parameters.AddWithValue($"$channel{index}", search.Zones[index].Channel);
                command.Parameters.AddWithValue($"$zone{index}", search.Zones[index].Id);
            }
            conditions.Add("EXISTS (SELECT 1 FROM RecordingZones z WHERE z.RecordingId=r.Id AND (r.Channel,z.ZoneId) IN (VALUES " + string.Join(",", zones) + "))");
        }
        var order = oldestFirst ? "ASC" : "DESC";
        command.CommandText = $"SELECT Metadata, FileBasePath FROM Recordings r WHERE {string.Join(" AND ", conditions)} ORDER BY TriggeredAt {order}, Id {order} LIMIT $limit";
        command.Parameters.AddWithValue("$limit", search.Limit);
        return Rows(command);

        void Add(string column, object? value)
        {
            if (value is not null)
            {
                conditions.Add(column + "=$" + column);
                command.Parameters.AddWithValue("$" + column, value);
            }
        }
    }) ?? [];

    public RecordingEvent[] FindByRequest(string requestId) => Read(command =>
    {
        command.CommandText = """
            SELECT Metadata, FileBasePath FROM Recordings r WHERE ConfigurationId=$configuration
            AND EXISTS (SELECT 1 FROM RecordingMarkers m WHERE m.RecordingId=r.Id AND m.RequestId=$request)
            ORDER BY TriggeredAt DESC
            """;
        command.Parameters.AddWithValue("$request", requestId);
        return Rows(command);
    }) ?? [];

    public object[] Cameras() => Read(command =>
    {
        command.CommandText = """
            SELECT Channel, CameraName FROM (
                SELECT Channel, CameraName, ROW_NUMBER() OVER (PARTITION BY Channel ORDER BY TriggeredAt DESC) AS Position
                FROM Recordings WHERE ConfigurationId=$configuration) WHERE Position=1 ORDER BY Channel
            """;
        using var reader = command.ExecuteReader();
        var cameras = new List<object>();
        while (reader.Read())
        {
            cameras.Add(new
            {
                channel = reader.GetInt32(0),
                name = reader.GetString(1)
            });
        }
        return cameras.ToArray();
    }) ?? [];

    public RecordingZoneGroup[] ZoneGroups() => Read(command =>
    {
        command.CommandText = """
            SELECT Channel, CameraName, ZoneId, Name FROM (
                SELECT r.Channel, r.CameraName, z.ZoneId, z.Name,
                    ROW_NUMBER() OVER (PARTITION BY r.Channel, z.ZoneId ORDER BY r.TriggeredAt DESC) AS Position
                FROM Recordings r JOIN RecordingZones z ON r.Id=z.RecordingId
                WHERE ConfigurationId=$configuration) WHERE Position=1 ORDER BY Channel
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<(int Channel, string CameraName, EventZone Zone)>();
        while (reader.Read())
        {
            rows.Add((reader.GetInt32(0), reader.GetString(1), new(reader.GetString(2), reader.GetString(3))));
        }
        return rows.GroupBy(row => row.Channel).Select(group =>
            new RecordingZoneGroup(group.Key, group.First().CameraName, [.. group.Select(row => row.Zone)])).ToArray();
    }) ?? [];

    public RecordingCatalogTotals Totals() => Read(command =>
    {
        command.CommandText = """
            SELECT COALESCE(SUM(SizeBytes),0),
                COALESCE(SUM(SizeBytes * CASE WHEN Status IN ('recording','finalizing') THEN 2 ELSE 1 END),0), COUNT(*)
            FROM Recordings WHERE ConfigurationId=$configuration
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new RecordingCatalogTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2));
    }) ?? new(0, 0, 0);

    internal void Migrate(string source) => Execute(() =>
    {
        var original = Path.Combine(source, "archive.db");
        if (!File.Exists(original))
        {
            return;
        }
        Initialize();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$configuration", configurationId);
        command.CommandText = "SELECT ConfigurationId FROM RecordingLocationMigrations WHERE ConfigurationId=$configuration";
        if (command.ExecuteScalar() is not null)
        {
            return;
        }
        // Snapshot the legacy catalogue in the registered archive. Opening the old file read-only
        // also prevents SQLite from creating it at an unmounted path.
        var snapshot = _databasePath + ".legacy-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = original, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = snapshot, Pooling = false
            }.ToString()))
            {
                legacy.Open();
                copy.Open();
                legacy.BackupDatabase(copy);
            }
            command.CommandText = "ATTACH DATABASE $source AS legacy";
            command.Parameters.AddWithValue("$source", snapshot);
            command.ExecuteNonQuery();
            try
            {
                command.CommandText = "PRAGMA legacy.user_version";
                if ((long)command.ExecuteScalar()! > 1)
                {
                    throw new IOException("This recording catalogue was created by a newer server version.");
                }
                using var transaction = connection.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO Recordings SELECT * FROM legacy.Recordings WHERE ConfigurationId=$configuration;
                    INSERT OR IGNORE INTO RecordingMarkers
                        SELECT m.* FROM legacy.RecordingMarkers m JOIN legacy.Recordings r ON m.RecordingId=r.Id
                        WHERE r.ConfigurationId=$configuration;
                    INSERT OR IGNORE INTO RecordingZones
                        SELECT z.* FROM legacy.RecordingZones z JOIN legacy.Recordings r ON z.RecordingId=r.Id
                        WHERE r.ConfigurationId=$configuration;
                    INSERT INTO RecordingLocationMigrations VALUES ($configuration);
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            finally
            {
                command.Transaction = null;
                command.CommandText = "DETACH DATABASE legacy";
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            File.Delete(snapshot);
        }
    });

    public RecordingEvent[] CompletedAfter(RecordingEvent? cursor, int limit) => Read(command =>
    {
        command.CommandText = """
            SELECT Metadata, FileBasePath FROM Recordings
            WHERE ConfigurationId=$configuration AND Status IN ('ready','failed')
              AND ($timestamp IS NULL OR (TriggeredAt, Id) < ($timestamp, $id))
            ORDER BY TriggeredAt DESC, Id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$timestamp", (object?)cursor?.TriggeredAt.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", (object?)cursor?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        return Rows(command);
    }) ?? [];

    public bool WasPruned(string id) => Read(command =>
    {
        command.CommandText = "SELECT Id FROM RecordingPruned WHERE ConfigurationId=$configuration AND Id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }) is not null;

    public void RememberPruned(RecordingEvent recording) => Execute(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        MarkPruned(command, recording);
    });

    private static void MarkPruned(SqliteCommand command, RecordingEvent recording)
    {
        command.Parameters.Clear();
        command.CommandText = "INSERT OR IGNORE INTO RecordingPruned VALUES ($configuration,$id)";
        command.Parameters.AddWithValue("$configuration", recording.ConfigurationId);
        command.Parameters.AddWithValue("$id", recording.Id);
        command.ExecuteNonQuery();
    }

    public void Save(RecordingEvent recording, RecordingTrigger[]? markers = null) => Execute(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Recordings VALUES ($id,$configuration,$camera,$channel,$name,$started,$triggered,$ended,$status,$protected,$size,$path,$metadata)
            ON CONFLICT(Id) DO UPDATE SET EndedAt=$ended,Status=$status,Protected=$protected,SizeBytes=$size,Metadata=$metadata;
            """;
        command.Parameters.AddWithValue("$id", recording.Id);
        command.Parameters.AddWithValue("$configuration", recording.ConfigurationId);
        command.Parameters.AddWithValue("$camera", recording.CameraId);
        command.Parameters.AddWithValue("$channel", recording.Channel);
        command.Parameters.AddWithValue("$name", recording.CameraName);
        command.Parameters.AddWithValue("$started", recording.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("$triggered", recording.TriggeredAt.UtcTicks);
        command.Parameters.AddWithValue("$ended", (object?)recording.EndedAt?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", recording.Status);
        command.Parameters.AddWithValue("$protected", recording.Protected);
        command.Parameters.AddWithValue("$size", recording.SizeBytes);
        command.Parameters.AddWithValue("$path", recording.FileBasePath);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(recording));
        command.ExecuteNonQuery();
        foreach (var trigger in (markers ?? []).Concat(recording.AdditionalTriggers.Prepend(recording.Trigger)))
        {
            InsertMarker(connection, transaction, recording.Id, trigger);
        }
        transaction.Commit();
    });

    public void AddMarker(string recordingId, RecordingTrigger trigger) => Execute(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertMarker(connection, transaction, recordingId, trigger);
        transaction.Commit();
    });

    public RecordingTrigger[] Markers(string recordingId, bool all = false) => Read(command =>
    {
        command.CommandText = """
            SELECT m.Metadata FROM RecordingMarkers m JOIN Recordings r ON r.Id=m.RecordingId
            WHERE r.ConfigurationId=$configuration AND r.Id=$id ORDER BY m.Timestamp,m.Id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$id", recordingId);
        command.Parameters.AddWithValue("$limit", all ? -1 : 8192);
        using var reader = command.ExecuteReader();
        var triggers = new List<RecordingTrigger>();
        while (reader.Read())
        {
            triggers.Add(JsonSerializer.Deserialize<RecordingTrigger>(reader.GetString(0))!);
        }
        return triggers.ToArray();
    }) ?? [];

    public void Delete(string id, RecordingEvent? pruned = null) => Execute(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (pruned is not null)
        {
            MarkPruned(command, pruned);
        }
        command.Parameters.Clear();
        command.CommandText = "DELETE FROM Recordings WHERE ConfigurationId=$configuration AND Id=$id";
        command.Parameters.AddWithValue("$configuration", configurationId);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        transaction.Commit();
    });

    public RecordingTimeline Timeline(int? channel, DateTimeOffset since, DateTimeOffset before, string? cameraId = null, bool savedReplayOnly = false) => Read(command =>
    {
        var cameraColumn = channel is null ? "CameraId" : "Channel";
        command.CommandText = $"""
            SELECT Metadata, FileBasePath FROM Recordings WHERE ConfigurationId=$configuration
            AND {cameraColumn}=$camera
            {(savedReplayOnly ? "AND json_extract(Metadata, '$.Trigger.Source') = 'replay'" : "")}
            AND StartedAt < $before AND COALESCE(EndedAt,$now) > $since
            ORDER BY StartedAt,Id LIMIT 2001
            """;
        command.Parameters.AddWithValue("$camera", (object?)channel ?? cameraId!);
        command.Parameters.AddWithValue("$since", since.UtcTicks);
        command.Parameters.AddWithValue("$before", before.UtcTicks);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        var rows = Rows(command);
        return new RecordingTimeline(rows.Take(2000).ToArray(), rows.Length > 2000);
    }) ?? new([], false);

    private static void InsertMarker(SqliteConnection connection, SqliteTransaction transaction, string recordingId, RecordingTrigger trigger)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO RecordingMarkers VALUES ($recording,$id,$timestamp,$request,$metadata)";
        command.Parameters.AddWithValue("$recording", recordingId);
        command.Parameters.AddWithValue("$id", trigger.Id);
        command.Parameters.AddWithValue("$timestamp", trigger.Timestamp.UtcTicks);
        command.Parameters.AddWithValue("$request", (object?)trigger.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(trigger));
        command.ExecuteNonQuery();
        foreach (var zone in trigger.Zones ?? [])
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO RecordingZones VALUES ($recording,$zone,$name) ON CONFLICT(RecordingId,ZoneId) DO UPDATE SET Name=$name";
            command.Parameters.AddWithValue("$recording", recordingId);
            command.Parameters.AddWithValue("$zone", zone.Id);
            command.Parameters.AddWithValue("$name", zone.Name);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private T? Read<T>(Func<SqliteCommand, T?> read) where T : class
    {
        if (!Exists)
        {
            return null;
        }
        return Execute(() =>
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$configuration", configurationId);
            return read(command);
        });
    }

    private static RecordingEvent[] Rows(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var recordings = new List<RecordingEvent>();
        while (reader.Read())
        {
            recordings.Add(JsonSerializer.Deserialize<RecordingEvent>(reader.GetString(0))! with
            {
                FileBasePath = reader.GetString(1)
            });
        }
        return recordings.ToArray();
    }

    private static void Execute(Action action) => Execute(() => { action(); return true; });

    private static T Execute<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException exception)
        {
            throw new IOException("The recording catalogue could not be read or updated. Check disk space and permissions.", exception);
        }
    }
}
