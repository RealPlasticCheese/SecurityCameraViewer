using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record LiveRewindSettings(bool Enabled = false, int Seconds = 120, long MegabytesPerCamera = 128)
{
    public string? Validate() => Seconds < 1 ? "Enter a positive live rewind duration in seconds." :
        MegabytesPerCamera < 1 || MegabytesPerCamera > long.MaxValue / (1024 * 1024)
            ? "Enter a positive memory budget in MiB per camera." : null;
}

public sealed record RewindRange(string Epoch, long Start, long End);
public sealed record RewindFragment(long Sequence, long Start, double DurationSeconds, double MediaStartSeconds, double ClockRate = 1)
{
    public long End => Start + (long)Math.Round(DurationSeconds * ClockRate * 1000);
}
public sealed record RewindPlaylist(string Epoch, string MimeType, bool HasAudio, RewindFragment[] Fragments);
public sealed record RewindCamera(int Channel, string Status, string Message, long Bytes, RewindRange[] Ranges);

/// <summary>Retains immutable fragments and their keyframe boundaries, with separate initialization for each stream epoch.
/// Each fragment retains its production-to-presentation mapping independently of hub delivery.</summary>
public sealed class LiveRewindBuffer(TimeSpan retention, long maximumBytes, TimeProvider? timeProvider = null)
{
    private const long MinimumTimingUncertaintyMilliseconds = 100;
    private readonly object _sync = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Queue<Entry> _entries = new();
    private readonly Dictionary<string, Epoch> _epochs = [];
    private Epoch? _current;
    private long _sequence;
    private long _bytes;

    public void Initialize(LiveMediaInitialization initialization)
    {
        if (initialization.Bytes.Length > Math.Min(1024 * 1024, maximumBytes))
        {
            throw new InvalidDataException("Live initialization exceeds its buffer limit.");
        }

        lock (_sync)
        {
            Prune();
            if (_current is { Count: 0 })
            {
                RemoveEpoch(_current);
            }

            _current = new(Guid.NewGuid().ToString("N"), initialization);
            _epochs.Add(_current.Id, _current);
            _bytes += initialization.Bytes.Length;
            Prune();
        }
    }

    public void Add(VideoFragment fragment)
    {
        if (fragment.Bytes.Length > maximumBytes || !double.IsFinite(fragment.StartSeconds) || fragment.StartSeconds < 0 ||
            !double.IsFinite(fragment.DurationSeconds) || fragment.DurationSeconds is <= 0 or > 30)
        {
            throw new InvalidDataException("Invalid live rewind fragment.");
        }

        lock (_sync)
        {
            var epoch = _current ?? throw new InvalidOperationException("Initialize live rewind before adding fragments.");
            if (epoch.Count == 0 && !fragment.StartsWithKeyframe)
            {
                return;
            }
            var timing = epoch.Timeline.Map(fragment);
            var previousEntry = epoch.LastEntry;
            // Startup calibration may remove initial flush latency. Once established, only the
            // neighboring fragment can be refined; older paused footage keeps its timeline.
            var refinements = epoch.Calibrated
                ? previousEntry is null ? [] : new[] { previousEntry }
                : _entries.Where(entry => entry.Epoch == epoch).ToArray();
            foreach (var retained in refinements)
            {
                if (retained == previousEntry || !epoch.Calibrated &&
                    (timing.OriginMilliseconds < retained.Timing.OriginMilliseconds || timing.ClockRate != retained.Timing.ClockRate))
                {
                    retained.Metadata = retained.Metadata with
                    {
                        Start = timing.Time(retained.Metadata.MediaStartSeconds), ClockRate = timing.ClockRate
                    };
                    retained.Timing = timing;
                }
            }
            if (!epoch.Calibrated)
            {
                epoch.Calibrated = epoch.Timeline.Calibrated || ++epoch.FragmentsSeen >= 4;
            }
            var start = timing.Time(fragment.StartSeconds);
            var entry = new Entry(epoch, new(++_sequence, start, fragment.DurationSeconds, fragment.StartSeconds, timing.ClockRate), fragment.Bytes,
                fragment.StartsWithKeyframe, timing);
            if (previousEntry is not null && start < previousEntry.Metadata.Start)
            {
                throw new InvalidDataException("Live rewind timestamps went backwards.");
            }

            _entries.Enqueue(entry);
            epoch.LastEntry = entry;
            epoch.Count++;
            _bytes += fragment.Bytes.Length;
            Prune();
        }
    }

    public RewindCamera Snapshot(int channel, string status, string message)
    {
        lock (_sync)
        {
            Prune();
            var ranges = new List<RewindRange>();
            Entry? previousEntry = null;
            var decodable = false;
            foreach (var entry in PlaybackEntries())
            {
                if (previousEntry is null || previousEntry.Epoch != entry.Epoch ||
                    previousEntry.Metadata.MediaStartSeconds + previousEntry.Metadata.DurationSeconds + 0.000001 < entry.Metadata.MediaStartSeconds)
                {
                    decodable = false;
                }
                decodable |= entry.StartsWithKeyframe;
                previousEntry = entry;
                if (!decodable)
                {
                    continue;
                }
                var end = End(entry);
                if (ranges.LastOrDefault() is { } previous && previous.Epoch == entry.Epoch.Id && entry.Metadata.Start <= previous.End)
                {
                    ranges[^1] = previous with
                    {
                        End = Math.Max(previous.End, end)
                    };
                }
                else
                {
                    ranges.Add(new(entry.Epoch.Id, entry.Metadata.Start, end));
                }
            }

            return new(channel, status, message, _bytes, [.. ranges]);
        }
    }

    public RewindPlaylist? Playlist(string epochId, long at)
    {
        lock (_sync)
        {
            Prune();
            return ReadWindow(epochId, at)?.Playlist;
        }
    }

    public RewindPlaybackWindow? Window(string epochId, long at)
    {
        lock (_sync)
        {
            Prune();
            return ReadWindow(epochId, at);
        }
    }

    private RewindPlaybackWindow? ReadWindow(string epochId, long at)
    {
        if (!_epochs.TryGetValue(epochId, out var epoch))
        {
            return null;
        }
        var entries = PlaybackEntries().Where(entry => entry.Epoch == epoch).ToArray();
        var index = Array.FindIndex(entries, entry => entry.Metadata.Start <= at && at < End(entry));
        if (index < 0)
        {
            return null;
        }
        var requestedIndex = index;
        index = DecodeFrom(entries, index);
        if (index < 0)
        {
            return null;
        }
        var selected = entries.Skip(index).Take(requestedIndex - index + 12).ToArray();
        return new(new(epochId, epoch.Initialization.MimeType, epoch.Initialization.HasAudio,
            selected.Select(entry => entry.Metadata).ToArray()), epoch.Initialization.Bytes,
            selected.Select(entry => entry.Bytes).ToArray());
    }

    public byte[]? Media(string epochId, long? sequence)
    {
        lock (_sync)
        {
            Prune();
            return !_epochs.TryGetValue(epochId, out var epoch) || epoch.Count == 0 ? null : sequence is null
                ? epoch.Initialization.Bytes
                : _entries.FirstOrDefault(entry => entry.Epoch == epoch && entry.Metadata.Sequence == sequence)?.Bytes;
        }
    }

    /// <summary>Snapshots media references under the buffer lock; eviction cannot invalidate an admitted export.
    /// Reconnects and missing footage form separate clips with their original wall-clock times.</summary>
    public RecordingClip[] Select(long start, long end)
    {
        if (start >= end || start < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
            end > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            throw new ArgumentException("Choose two different valid playback times.");
        }

        lock (_sync)
        {
            Prune();
            var entries = PlaybackEntries();
            var clips = new List<RecordingClip>();
            for (var index = 0; index < entries.Length;)
            {
                var first = index++;
                var rangeEnd = End(entries[first]);
                while (index < entries.Length && entries[index].Epoch == entries[first].Epoch &&
                    entries[index].Metadata.Start <= rangeEnd &&
                    entries[index].Metadata.MediaStartSeconds <= entries[index - 1].Metadata.MediaStartSeconds + entries[index - 1].Metadata.DurationSeconds + 0.000001)
                {
                    rangeEnd = Math.Max(rangeEnd, End(entries[index++]));
                }

                // Interframes immediately after missing media cannot be decoded independently.
                // Keep later footage once the next keyframe restores a complete decode chain.
                while (first < index && DecodeFrom(entries, first) < 0)
                {
                    first++;
                }
                if (first == index)
                {
                    continue;
                }
                var clipStart = Math.Max(start, entries[first].Metadata.Start);
                var clipEnd = Math.Min(end, rangeEnd);
                if (clipStart >= clipEnd)
                {
                    continue;
                }

                var selected = first;
                while (selected + 1 < index && entries[selected + 1].Metadata.Start <= clipStart)
                {
                    selected++;
                }
                var from = DecodeFrom(entries, selected);
                if (from < 0)
                {
                    continue;
                }

                var media = entries.Skip(from).Take(index - from).TakeWhile(entry => entry.Metadata.Start < clipEnd)
                    .Select(entry => entry.Bytes).ToArray();
                var last = index - 1;
                while (last > selected && entries[last].Metadata.Start >= clipEnd)
                {
                    last--;
                }
                var mediaStart = MediaAt(entries[selected].Metadata, clipStart);
                var mediaEnd = MediaAt(entries[last].Metadata, clipEnd);
                var clockRate = (clipEnd - clipStart) / ((mediaEnd - mediaStart) * 1000);
                // UTC metadata is rounded to milliseconds. Do not change encoding pace
                // solely to compensate for that rounding at the selected boundaries.
                if (Math.Abs((mediaEnd - mediaStart) * 1000 - (clipEnd - clipStart)) <= 1)
                {
                    clockRate = 1;
                }
                clips.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(clipStart),
                    DateTimeOffset.FromUnixTimeMilliseconds(clipEnd),
                    mediaStart - entries[from].Metadata.MediaStartSeconds,
                    entries[first].Epoch.Initialization.Bytes, media, clockRate));
            }
            return [.. clips];
        }
    }

    private static double MediaAt(RewindFragment fragment, long at) =>
        fragment.MediaStartSeconds + (at - fragment.Start) / (fragment.ClockRate * 1000);

    private Entry[] PlaybackEntries()
    {
        var entries = _entries.ToArray();
        for (var index = 0; index + 1 < entries.Length; index++)
        {
            var entry = entries[index];
            var next = entries[index + 1];
            var gap = next.Metadata.Start - End(entry);
            // Preserve the existing tolerance for short fragments. Longer neighboring
            // mappings also carry the cadence uncertainty accepted by the clock estimator;
            // their combined calibration errors can exceed a fixed 100 ms cutoff.
            var uncertainty = Math.Max(MinimumTimingUncertaintyMilliseconds,
                Math.Ceiling((entry.Metadata.DurationSeconds * entry.Metadata.ClockRate +
                    next.Metadata.DurationSeconds * next.Metadata.ClockRate) * 1000 * FragmentTimeline.MaximumRelativeIntervalVariation));
            if (entry.Epoch != next.Epoch || gap <= 0 || gap > uncertainty ||
                Math.Abs(entry.Metadata.MediaStartSeconds + entry.Metadata.DurationSeconds - next.Metadata.MediaStartSeconds) > 0.000001)
            {
                continue;
            }

            // Coverage, playlist seeks and exports must describe the same playable
            // interval. Stretch only the UTC mapping within the cadence uncertainty;
            // the media bytes, media duration and retained capture metadata stay intact.
            var metadata = entry.Metadata with
            {
                ClockRate = (next.Metadata.Start - entry.Metadata.Start) / (entry.Metadata.DurationSeconds * 1000)
            };
            entries[index] = new(entry.Epoch, metadata, entry.Bytes, entry.StartsWithKeyframe, entry.Timing);
        }
        return entries;
    }

    private static int DecodeFrom(Entry[] entries, int index)
    {
        while (!entries[index].StartsWithKeyframe)
        {
            if (index == 0 || entries[index - 1].Epoch != entries[index].Epoch ||
                entries[index - 1].Metadata.MediaStartSeconds + entries[index - 1].Metadata.DurationSeconds + 0.000001 < entries[index].Metadata.MediaStartSeconds)
            {
                return -1;
            }
            index--;
        }
        return index;
    }

    private void Prune()
    {
        var cutoff = _time.GetUtcNow().Subtract(retention).ToUnixTimeMilliseconds();
        while (_entries.TryPeek(out var oldest) && (End(oldest) <= cutoff || _bytes > maximumBytes || !oldest.StartsWithKeyframe))
        {
            _entries.Dequeue();
            _bytes -= oldest.Bytes.Length;
            oldest.Epoch.Count--;
            if (oldest.Epoch.Count == 0)
            {
                oldest.Epoch.LastEntry = null;
            }
            if (oldest.Epoch.Count == 0 && oldest.Epoch != _current)
            {
                RemoveEpoch(oldest.Epoch);
            }
        }
    }

    private void RemoveEpoch(Epoch epoch)
    {
        _epochs.Remove(epoch.Id);
        _bytes -= epoch.Initialization.Bytes.Length;
    }

    private static long End(Entry entry) => entry.Metadata.End;

    private sealed class Epoch(string id, LiveMediaInitialization initialization)
    {
        public string Id { get; } = id;
        public LiveMediaInitialization Initialization { get; } = initialization;
        public FragmentTimeline Timeline { get; } = new();
        public int Count
        {
            get; set;
        }
        public bool Calibrated { get; set; }
        public int FragmentsSeen { get; set; }
        public Entry? LastEntry { get; set; }
    }

    private sealed class Entry(Epoch epoch, RewindFragment metadata, byte[] bytes, bool startsWithKeyframe, FragmentTiming timing)
    {
        public Epoch Epoch { get; } = epoch;
        public RewindFragment Metadata { get; set; } = metadata;
        public byte[] Bytes { get; } = bytes;
        public bool StartsWithKeyframe { get; } = startsWithKeyframe;
        public FragmentTiming Timing { get; set; } = timing;
    }
}
