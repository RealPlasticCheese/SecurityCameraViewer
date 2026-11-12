using System.Collections.Concurrent;
using System.Text.Json;

namespace SecurityCameraViewer.Recording;

/// <summary>Coordinates owned media files, finalization and retention through a separate durable catalogue.
/// Only active captures are retained in memory; fragments never update the database.</summary>
public sealed class FileRecordingStore : IRecordingEventStore
{
    private readonly RecordingSettings _settings;
    private readonly FfmpegProcess _ffmpeg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, RecordingEvent> _active = new();
    private readonly IRecordingCatalog _catalog;
    private Lazy<object[]> _cameraCache;
    private readonly RecordingFileLayout _layout;
    private long _usedBytes;
    private long _reservedBytes;
    private int _count;
    private bool _prunedExistingDateFolders;
    private readonly string _directory;
    private readonly RecordingLocation _location;
    private readonly bool _rememberPruned;
    private readonly ICameraEventPublisher _publisher;
    public string Notice { get; private set; } = "";
    public long UsedBytes => Interlocked.Read(ref _usedBytes);
    public int Count => Volatile.Read(ref _count);
    public object[] Cameras => Volatile.Read(ref _cameraCache).Value;

    public FileRecordingStore(RecordingSettings settings, FfmpegProcess ffmpeg, ICameraEventPublisher? publisher = null,
        IRecordingCatalog? catalog = null, RecordingLocation? location = null)
    {
        _settings = settings;
        _ffmpeg = ffmpeg;
        _publisher = publisher ?? NullCameraEventPublisher.Instance;
        _location = location ?? new(settings.Directory, settings.MaximumStorageGigabytes);
        _rememberPruned = _location.Id is not null;
        _layout = new(_location.Directory, _rememberPruned ? Path.GetFileName(_location.ArchiveDirectory) : null);
        _directory = _layout.DirectoryPath;
        _catalog = catalog ?? new SqliteRecordingCatalog(_location.ArchiveDirectory, settings.ConfigurationId, !_rememberPruned);
        _cameraCache = new(_catalog.Cameras);
        try
        {
            _location.EnsureAvailable();
            ImportLegacyEvents();
            if (_catalog.Exists)
            {
                _catalog.Initialize();
                var totals = _catalog.Totals();
                _usedBytes = totals.Bytes;
                _reservedBytes = totals.ReservedBytes;
                _count = totals.Count;
                foreach (var item in _catalog.Search(new()
                {
                    Limit = int.MaxValue
                }, status: "recording")
                    .Concat(_catalog.Search(new()
                    {
                        Limit = int.MaxValue
                    }, status: "finalizing")))
                {
                    var recovered = Recover(item);
                    if (recovered is not null)
                    {
                        SaveAsync(recovered, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Delete(item);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notice = "The recording library cannot be read. Check its folder permissions and catalogue.";
            throw new IOException(Notice, ex);
        }
    }

    private void ImportLegacyEvents()
    {
        var directory = Path.Combine(_directory, "events");
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id))
            {
                continue;
            }

            try
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024)
                {
                    continue;
                }

                using var metadata = JsonDocument.Parse(File.ReadAllText(file));
                var item = metadata.RootElement.Deserialize<RecordingEvent>();
                if (item is null || item.Id != id)
                {
                    continue;
                }
                // Preserve explicit legacy ownership when configurations shared a recording root.
                if (metadata.RootElement.TryGetProperty(nameof(RecordingEvent.ConfigurationId), out _) &&
                    item.ConfigurationId != _settings.ConfigurationId)
                {
                    continue;
                }
                if (_catalog.Find(id) is not null)
                {
                    continue;
                }
                item = item with
                {
                    ConfigurationId = _settings.ConfigurationId,
                    FileBasePath = Path.Combine("events", id),
                    Mode = item.Trigger.Source == "stream" ? RecordingModes.AvailableVideo : item.Mode,
                    Trigger = item.Trigger with
                    {
                        Timestamp = item.TriggeredAt
                    }
                };
                if (Recover(item) is { } recovered)
                {
                    _catalog.Initialize();
                    _catalog.Save(recovered);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Notice = "Some recording metadata could not be loaded.";
            }
        }
    }

    private RecordingEvent? Recover(RecordingEvent item)
    {
        if (File.Exists(PathFor(item, ".mp4")))
        {
            // The final rename may have completed before the catalogue transition.
            File.Delete(PathFor(item, ".capture.mp4"));
            File.Delete(PathFor(item, ".export.tmp.mp4"));
            return item with
            {
                Status = "ready",
                SizeBytes = FileBytes(item)
            };
        }
        return File.Exists(PathFor(item, ".capture.mp4"))
            ? item with
            {
                Status = "finalizing",
                Interrupted = true,
                SizeBytes = FileBytes(item)
            }
            : null;
    }

    public RecordingEvent[] FindByRequest(string id) => _catalog.FindByRequest(id).Select(Current).ToArray();
    public RecordingEvent? Find(string id) => ValidId(id) ? _active.GetValueOrDefault(id) ?? _catalog.Find(id) : null;
    private RecordingEvent Current(RecordingEvent item) => _active.GetValueOrDefault(item.Id) ?? item;
    public RecordingTrigger[] Markers(string id) => ValidId(id) ? _catalog.Markers(id) : [];
    public RecordingTimeline Timeline(int channel, DateTimeOffset since, DateTimeOffset before, bool savedReplayOnly = false) =>
        Timeline(channel, null, since, before, savedReplayOnly);

    public RecordingTimeline TimelineByCameraId(string cameraId, DateTimeOffset since, DateTimeOffset before) =>
        Timeline(null, cameraId, since, before);

    private RecordingTimeline Timeline(int? channel, string? cameraId, DateTimeOffset since, DateTimeOffset before, bool savedReplayOnly = false)
    {
        if (channel < 1 || since >= before || before - since > TimeSpan.FromDays(2))
        {
            throw new ArgumentException("Choose a camera and a recording time range of up to two days.");
        }
        var result = _catalog.Timeline(channel, since, before, cameraId, savedReplayOnly);
        return result with
        {
            Recordings = [.. result.Recordings.Select(Current)]
        };
    }

    public RecordingEvent[] List(int? channel, DateTimeOffset? before, int limit = 50) =>
        _catalog.Search(new()
        {
            Channel = channel,
            Before = before,
            Limit = Math.Clamp(limit, 1, 100)
        }).Select(Current).ToArray();

    public RecordingEvent[] Search(RecordingSearch search)
    {
        if (search.Validate() is { } error)
        {
            throw new ArgumentException(error);
        }

        return _catalog.Search(search).Select(Current).ToArray();
    }

    public RecordingZoneGroup[] ZoneGroups() => _catalog.ZoneGroups();

    public RecordingEvent[] ListByCameraId(string? cameraId, DateTimeOffset? before, int limit = 50) =>
        _catalog.Search(new()
        {
            Before = before,
            Limit = Math.Clamp(limit, 1, 100)
        }, cameraId).Select(Current).ToArray();

    public string? MediaPath(string id, bool thumbnail = false)
    {
        if (Find(id) is not { } item || item.Status is not ("ready" or "failed"))
        {
            return null;
        }

        var path = PathFor(item, thumbnail ? ".jpg" : item.Status == "failed" ? ".capture.mp4" : ".mp4");
        return File.Exists(path) ? path : null;
    }

    public Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization, double preSeconds, CancellationToken token, DateTimeOffset? triggeredAt = null) =>
        BeginTriggeredAsync(channel, name, initialization, preSeconds, new(), token, triggeredAt);

    public async Task<IEventWriter> BeginTriggeredAsync(int channel, string name, byte[] initialization, double preSeconds, RecordingTrigger trigger, CancellationToken token, DateTimeOffset? triggeredAt = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            PrepareDirectory(_directory);
            _catalog.Initialize();
            EnsureCapacity(initialization.Length);
            var now = DateTimeOffset.UtcNow;
            var item = new RecordingEvent
            {
                Trigger = trigger,
                Channel = channel,
                CameraName = name,
                StartedAt = _settings.Camera(channel).EffectiveMode == RecordingModes.Event ? now.AddSeconds(-preSeconds) : triggeredAt ?? now,
                TriggeredAt = triggeredAt ?? now,
                ConfigurationId = _settings.ConfigurationId,
                SourceId = _settings.Camera(channel).CameraId,
                Mode = _settings.Camera(channel).EffectiveMode
            };
            item = item with
            {
                FileBasePath = _layout.CreateBasePath(item)
            };
            PrepareDirectory(Path.GetDirectoryName(PathFor(item, ".capture.mp4"))!);
            await SaveAsync(item, token);
            FileStream? stream = null;
            try
            {
                stream = new FileStream(PathFor(item, ".capture.mp4"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                await stream.WriteAsync(initialization, token);
                UpdateActive(item with
                {
                    SizeBytes = initialization.Length
                });
                Publish("recording.started", item);
                return new Writer(this, item.Id, stream);
            }
            catch
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync();
                }

                var failed = item with
                {
                    Status = "failed",
                    Interrupted = true,
                    SizeBytes = FileBytes(item)
                };
                await SaveAsync(failed, CancellationToken.None);
                Publish("recording.failed", failed);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Imports a completed batch into the ordinary library. Failed imports roll back the entire batch.</summary>
    public async Task<RecordingEvent[]> ImportAsync(RecordingImport[] imports, CancellationToken token)
    {
        if (imports.Length == 0 || imports.Any(import => import.Recording.ConfigurationId != _settings.ConfigurationId ||
            import.Recording.StartedAt >= import.Recording.EndedAt || import.Recording.DurationSeconds <= 0 ||
            !ValidId(import.Recording.Id)) || imports.DistinctBy(import => import.Recording.Id).Count() != imports.Length)
        {
            throw new ArgumentException("Supply completed recordings for this library.");
        }

        await _gate.WaitAsync(token);
        var items = new List<RecordingEvent>();
        try
        {
            if (imports.Any(import => Find(import.Recording.Id) is not null))
            {
                throw new ArgumentException("A recording with this ID already exists.");
            }
            var total = imports.Sum(import => new FileInfo(import.VideoPath).Length +
                (File.Exists(import.VideoPath + ".jpg") ? new FileInfo(import.VideoPath + ".jpg").Length : 0));
            if (total == 0 || imports.Any(import => new FileInfo(import.VideoPath).Length == 0))
            {
                throw new IOException("The selected footage could not be converted into a video.");
            }
            PrepareDirectory(_directory);
            _catalog.Initialize();
            EnsureCapacity(total);
            foreach (var import in imports)
            {
                token.ThrowIfCancellationRequested();
                var item = import.Recording with
                {
                    Status = "ready",
                    FileBasePath = _layout.CreateBasePath(import.Recording),
                    SizeBytes = new FileInfo(import.VideoPath).Length +
                        (File.Exists(import.VideoPath + ".jpg") ? new FileInfo(import.VideoPath + ".jpg").Length : 0)
                };
                items.Add(item);
                PrepareDirectory(Path.GetDirectoryName(PathFor(item, ".mp4"))!);
                await CopyAsync(import.VideoPath, PathFor(item, ".export.tmp.mp4"), token);
                if (File.Exists(import.VideoPath + ".jpg"))
                {
                    await CopyAsync(import.VideoPath + ".jpg", PathFor(item, ".jpg"), token);
                }
                File.Move(PathFor(item, ".export.tmp.mp4"), PathFor(item, ".mp4"));
                await SaveAsync(item, token);
            }
            foreach (var item in items)
            {
                Publish("recording.ready", item);
            }
            return [.. items];
        }
        catch
        {
            foreach (var item in items)
            {
                if (Find(item.Id) is not null)
                {
                    Delete(item);
                }
                else
                {
                    foreach (var suffix in new[] { ".mp4", ".jpg", ".export.tmp.mp4" })
                    {
                        File.Delete(PathFor(item, suffix));
                    }
                }
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken token, bool durable = false)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        await input.CopyToAsync(output, token);
        if (durable)
        {
            token.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
        }
    }

    private void PrepareDirectory(string directory)
    {
        _location.EnsureAvailable();
        if (!_rememberPruned)
        {
            Directory.CreateDirectory(directory);
        }
    }

    internal RecordingEvent[] CompletedAfter(RecordingEvent? cursor, int limit)
    {
        _location.EnsureAvailable();
        return _catalog.CompletedAfter(cursor, limit);
    }

    internal async Task CheckCapacityAsync(long minimumBytes, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _location.EnsureAvailable();
            EnsureCapacity(Math.Max(1, minimumBytes));
            if (_rememberPruned)
            {
                _catalog.Initialize();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task MirrorAsync(FileRecordingStore source, RecordingEvent recording, CancellationToken token)
    {
        _location.EnsureAvailable();
        if (Find(recording.Id) is { } existing)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (Find(recording.Id) is { } current && current.Protected != recording.Protected)
                {
                    await SaveAsync(current with { Protected = recording.Protected }, token);
                }
            }
            finally
            {
                _gate.Release();
            }
            return;
        }

        if (_catalog.WasPruned(recording.Id) ||
            (!recording.Protected && _settings.RetentionDays > 0 &&
             recording.TriggeredAt < DateTimeOffset.UtcNow.AddDays(-_settings.RetentionDays)))
        {
            return;
        }

        var item = recording with { FileBasePath = _layout.CreateBasePath(recording) };
        var videoSuffix = item.Status == "ready" ? ".mp4" : ".capture.mp4";
        var sourceVideo = source.MediaPath(recording.Id);
        if (sourceVideo is null)
        {
            return;
        }
        var sourceThumbnail = source.MediaPath(recording.Id, thumbnail: true);
        var markers = source._catalog.Markers(recording.Id, all: true);
        var video = PathFor(item, videoSuffix);
        var thumbnail = PathFor(item, ".jpg");
        var pendingVideo = video + ".mirror.tmp";
        var pendingThumbnail = thumbnail + ".mirror.tmp";
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                PrepareDirectory(Path.GetDirectoryName(video)!);
                _catalog.Initialize();
                File.Delete(pendingVideo);
                File.Delete(pendingThumbnail);
                var bytes = new FileInfo(sourceVideo).Length +
                    (sourceThumbnail is null ? 0 : new FileInfo(sourceThumbnail).Length);
                if (!MakeMirrorSpace(item, bytes))
                {
                    _catalog.RememberPruned(item);
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            await CopyAsync(sourceVideo, pendingVideo, token, durable: true);
            if (sourceThumbnail is not null)
            {
                await CopyAsync(sourceThumbnail, pendingThumbnail, token, durable: true);
            }
            item = item with { SizeBytes = new FileInfo(pendingVideo).Length +
                (sourceThumbnail is null ? 0 : new FileInfo(pendingThumbnail).Length) };

            await _gate.WaitAsync(token);
            try
            {
                _location.EnsureAvailable();
                if (Find(item.Id) is not null || _catalog.WasPruned(item.Id))
                {
                    return;
                }
                if (!MakeMirrorSpace(item, item.SizeBytes, staged: true))
                {
                    _catalog.RememberPruned(item);
                    return;
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    File.Move(pendingVideo, video, overwrite: true);
                    if (sourceThumbnail is not null)
                    {
                        File.Move(pendingThumbnail, thumbnail, overwrite: true);
                    }
                    // Finish the short catalogue transaction once the files have been published.
                    await SaveAsync(item, CancellationToken.None, markers);
                }
                catch
                {
                    _location.EnsureAvailable();
                    File.Delete(video);
                    File.Delete(thumbnail);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _location.EnsureAvailable();
            File.Delete(pendingVideo);
            File.Delete(pendingThumbnail);
        }
    }

    private bool MakeMirrorSpace(RecordingEvent item, long bytes, bool staged = false)
    {
        var limit = (long)(_settings.MaximumStorageGigabytes * 1_000_000_000);
        if (bytes > limit)
        {
            return false;
        }

        while (_reservedBytes + bytes > limit ||
            RecordingStorage.FreeBytes(_location.ArchiveDirectory) is { } available &&
            available < 64L * 1024 * 1024 + (staged ? 0 : bytes))
        {
            var candidates = new RecordingSearch { Limit = 1 };
            var oldest = _catalog.Search(candidates, status: "ready", oldestFirst: true, protectedOnly: false)
                .Concat(_catalog.Search(candidates, status: "failed", oldestFirst: true, protectedOnly: false))
                .OrderBy(recording => recording.TriggeredAt).ThenBy(recording => recording.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (oldest is null || !item.Protected &&
                (oldest.TriggeredAt > item.TriggeredAt || oldest.TriggeredAt == item.TriggeredAt &&
                 string.CompareOrdinal(oldest.Id, item.Id) >= 0))
            {
                if (oldest is null || item.Protected)
                {
                    throw new RecordingLocationException("full", "The mirror has insufficient space for this recording.");
                }
                return false;
            }
            Delete(oldest, remember: true);
        }
        return true;
    }

    public async Task<bool> ProtectAsync(string id, bool protect, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Find(id) is not { } item)
            {
                return false;
            }

            item = item with
            {
                Protected = protect
            };
            await SaveAsync(item, token);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Find(id) is not { } item || item.Protected || item.Status is "recording" or "finalizing")
            {
                return false;
            }

            Delete(item, remember: true);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MaintainAsync(CancellationToken token)
    {
        _location.EnsureAvailable();
        // Finalize serially; no unbounded process queue. Readers and active captures remain independent.
        foreach (var item in _active.Values.Where(e => e.Status == "finalizing").OrderBy(e => e.TriggeredAt))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMinutes(2));
                var temp = PathFor(item, ".export.tmp.mp4");
                double duration = item.DurationSeconds;
                if (item.Interrupted)
                {
                    // Metadata is written only on event transitions, so a crash may leave stale duration.
                    duration = 0;
                    await using var capture = File.OpenRead(PathFor(item, ".capture.mp4"));
                    await FragmentedMp4.ReadAsync(capture, _ => Task.CompletedTask, part => { duration += part.DurationSeconds; return Task.CompletedTask; }, deadline.Token);
                }
                await _ffmpeg.FileAsync(["-i", PathFor(item, ".capture.mp4"), "-map", "0:v:0", "-map", "0:a?", "-c", "copy",
                    "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", temp], deadline.Token);
                File.Move(temp, PathFor(item, ".mp4"), true);
                try
                {
                    var thumbnailSeconds = Math.Clamp((item.TriggeredAt - item.StartedAt).TotalSeconds, 0, Math.Max(0, duration - .1));
                    await _ffmpeg.FileAsync(["-i", PathFor(item, ".mp4"), "-ss", thumbnailSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        "-frames:v", "1", "-vf", "scale=320:-2", "-threads", "1", PathFor(item, ".jpg")], deadline.Token);
                }
                catch (IOException)
                {
                    /* A thumbnail failure must not hide playable footage. */
                }
                await _gate.WaitAsync(token);
                try
                {
                    File.Delete(PathFor(item, ".capture.mp4"));
                    var current = _active[item.Id] with
                    {
                        Status = "ready",
                        SizeBytes = FileBytes(item),
                        DurationSeconds = duration,
                        EndedAt = item.StartedAt.AddSeconds(duration)
                    };
                    await SaveAsync(current, token);
                    Publish("recording.ready", current);
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                await _gate.WaitAsync(token);
                try
                {
                    var current = _active[item.Id] with
                    {
                        Status = File.Exists(PathFor(item, ".mp4")) ? "ready" : "failed",
                        Interrupted = true,
                        SizeBytes = FileBytes(item)
                    };
                    try
                    {
                        await SaveAsync(current, token);
                    }
                    catch (IOException)
                    {
                    }
                    Publish(current.Status == "ready" ? "recording.ready" : "recording.failed", current);
                    Notice = "An interrupted event could not be fully finalized. Original captured data has been retained.";
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        await _gate.WaitAsync(token);
        try
        {
            // Sweep historical folders once per store; individual deletions prune their own paths.
            if (!_prunedExistingDateFolders)
            {
                _layout.PruneExistingDateFolders(_settings.ConfigurationId, token);
                _prunedExistingDateFolders = true;
            }
            EnsureCapacity(0);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(string type, RecordingEvent item) => _publisher.Publish(new(type, item.Channel, item.CameraName,
        [.. (item.Trigger.Zones ?? []).Concat(item.AdditionalTriggers.SelectMany(t => t.Zones ?? [])).DistinctBy(z => z.Id)], item.Id, new
        {
            item.Status,
            item.DurationSeconds,
            item.Trigger,
            item.AdditionalTriggers,
            videoUrl = $"/api/v1/recordings/{item.Id}/video"
        })
    {
        SourceId = item.CameraId,
        ConfigurationId = item.ConfigurationId
    });

    private void EnsureCapacity(long additional)
    {
        var limit = (long)(_settings.MaximumStorageGigabytes * 1_000_000_000);
        // Reserve room to remux active clips without exceeding the configured budget.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.RetentionDays);
        // Indexed batches are fetched only when age maintenance or quota admission needs eviction.
        EvictExpired("ready");
        EvictExpired("failed");
        while (_reservedBytes + additional * 2 > limit)
        {
            var candidate = Candidates("ready").Concat(Candidates("failed"))
                .Where(item => !item.Protected).OrderBy(item => item.TriggeredAt).FirstOrDefault();
            if (candidate is null)
            {
                throw new RecordingLocationException("full", "Recording storage limit reached. Free space or unprotect older recordings.", additional);
            }
            Delete(candidate, remember: true);
        }

        RecordingEvent[] Candidates(string status) => _catalog.Search(new()
        {
            Limit = 1
        }, status: status, oldestFirst: true, protectedOnly: false);
        void EvictExpired(string status)
        {
            // Age cleanup runs during maintenance, not for every arriving fragment.
            if (additional != 0 || _settings.RetentionDays == 0)
            {
                return;
            }
            foreach (var item in _catalog.Search(new()
            {
                Before = cutoff,
                Limit = int.MaxValue
            }, status: status, oldestFirst: true, protectedOnly: false))
            {
                Delete(item, remember: true);
            }
        }

        if (additional > 0)
        {
            if (RecordingStorage.FreeBytes(_directory) is { } available && available < 64L * 1024 * 1024 + additional * 2)
            {
                throw new RecordingLocationException("full", "Recording paused because the destination disk is nearly full.", additional);
            }
        }
    }

    private async Task AppendAsync(string id, FileStream stream, VideoFragment fragment, CancellationToken token)
    {
        _location.EnsureAvailable();
        await _gate.WaitAsync(token);
        try
        {
            EnsureCapacity(fragment.Bytes.Length);
            // Reserve space before releasing the store lock; concurrent cameras cannot overcommit the quota.
            UpdateActive(_active[id] with
            {
                SizeBytes = _active[id].SizeBytes + fragment.Bytes.Length
            });
        }
        finally
        {
            _gate.Release();
        }
        await stream.WriteAsync(fragment.Bytes, token);
        _active.AddOrUpdate(id, _ => throw new InvalidOperationException(), (_, current) => current with
        {
            DurationSeconds = current.DurationSeconds + fragment.DurationSeconds
        });
    }

    private async Task AddTriggerAsync(string id, RecordingTrigger trigger, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _location.EnsureAvailable();
            var current = _active[id];
            // Every activation is a timestamped marker, even when its zone was already seen in this clip.
            _catalog.AddMarker(id, trigger);
            if (trigger.Source == "motion")
            {
                var known = (current.Trigger.Zones ?? []).Concat(current.AdditionalTriggers.SelectMany(t => t.Zones ?? [])).Select(z => z.Id).ToHashSet();
                trigger = trigger with
                {
                    Zones = trigger.Zones?.Where(z => !known.Contains(z.Id)).ToArray()
                };
                if (trigger.Zones is null or { Length: 0 })
                {
                    return;
                }
            }
            if (current.AdditionalTriggers.Length >= 128)
            {
                // The separate marker catalogue retains further activations without growing capture memory.
                return;
            }

            current = current with
            {
                AdditionalTriggers = [.. current.AdditionalTriggers, trigger]
            };
            await SaveAsync(current, token);
        }
        finally
        {
            _gate.Release();
        }
    }
    private async Task FinishAsync(string id, bool interrupted, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var item = _active[id];
            item = item with
            {
                Status = "finalizing",
                Interrupted = interrupted,
                EndedAt = item.StartedAt.AddSeconds(item.DurationSeconds)
            };
            await SaveAsync(item, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task SaveAsync(RecordingEvent item, CancellationToken token, RecordingTrigger[]? markers = null)
    {
        token.ThrowIfCancellationRequested();
        _location.EnsureAvailable();
        var previous = Find(item.Id);
        _catalog.Save(item, markers);
        if (previous is null)
        {
            // Polling recording status must not enumerate the completed library every two seconds.
            Volatile.Write(ref _cameraCache, new(_catalog.Cameras));
        }
        Account(previous, item);
        if (item.Status is "recording" or "finalizing")
        {
            _active[item.Id] = item;
        }
        else
        {
            _active.TryRemove(item.Id, out _);
        }
        return Task.CompletedTask;
    }

    private void UpdateActive(RecordingEvent item)
    {
        Account(_active.GetValueOrDefault(item.Id), item);
        _active[item.Id] = item;
    }

    private void Account(RecordingEvent? previous, RecordingEvent? current)
    {
        static long Reservation(RecordingEvent? item) => item is null ? 0 :
            item.SizeBytes * (item.Status is "recording" or "finalizing" ? 2 : 1);
        Interlocked.Add(ref _usedBytes, (current?.SizeBytes ?? 0) - (previous?.SizeBytes ?? 0));
        _reservedBytes += Reservation(current) - Reservation(previous);
        Interlocked.Add(ref _count, (current is null ? 0 : 1) - (previous is null ? 0 : 1));
    }
    private void Delete(RecordingEvent item, bool remember = false)
    {
        _location.EnsureAvailable();
        foreach (var suffix in new[] { ".mp4", ".capture.mp4", ".export.tmp.mp4", ".jpg", ".json.tmp", ".json" })
        {
            File.Delete(PathFor(item, suffix));
        }

        _catalog.Delete(item.Id, remember && _rememberPruned ? item : null);
        Volatile.Write(ref _cameraCache, new(_catalog.Cameras));
        _active.TryRemove(item.Id, out _);
        Account(item, null);
        _layout.PruneRecordingDateFolders(item);
    }
    private long FileBytes(RecordingEvent item) => new[] { ".mp4", ".capture.mp4", ".export.tmp.mp4", ".jpg" }
        .Sum(suffix => File.Exists(PathFor(item, suffix)) ? new FileInfo(PathFor(item, suffix)).Length : 0);
    private string PathFor(RecordingEvent item, string suffix) => _layout.PathFor(item, suffix);
    private static bool ValidId(string id) => id.Length == 32 && Guid.TryParseExact(id, "N", out _);

    private sealed class Writer(FileRecordingStore owner, string id, FileStream stream) : IEventWriter
    {
        public string Id => id;
        public Task AddTriggerAsync(RecordingTrigger trigger, CancellationToken token) => owner.AddTriggerAsync(id, trigger, token);
        private bool _finished;
        public Task AppendAsync(VideoFragment fragment, CancellationToken token) => owner.AppendAsync(id, stream, fragment, token);
        public async Task CompleteAsync(bool interrupted, CancellationToken token)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            await stream.DisposeAsync();
            await owner.FinishAsync(id, interrupted, token);
        }
        public ValueTask DisposeAsync() => new(CompleteAsync(true, CancellationToken.None));
        public ValueTask CloseAsync() => stream.DisposeAsync();
    }

    internal static ValueTask AbandonAsync(IEventWriter writer) => ((Writer)writer).CloseAsync();
}
