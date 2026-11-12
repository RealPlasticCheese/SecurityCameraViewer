using System.Text.Json;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;

internal static class CameraSynchronizationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 11, 11, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        DeliveryAndStartup();
        Pacing();
        PausesAndJitter();
        JitterCoverage();
        CadenceScaledCoverage();
        CapturedJitterCoverage();
        CapturedNucRewindCoverage();
        RecentRewindAcrossTimingSeams();
        BrokenDecodeCoverage();
        PresentationAndRecovery();
        ExportsAndRetention();
        await ConcurrentReadersAsync();
        Console.WriteLine("All multi-camera synchronization checks passed.");
    }

    private static void DeliveryAndStartup()
    {
        var firstTime = new TestTime { Now = Origin.AddSeconds(1) };
        var secondTime = new TestTime { Now = Origin.AddSeconds(3) };
        var first = Buffer(firstTime);
        var second = Buffer(secondTime);
        for (var index = 0; index < 20; index++)
        {
            firstTime.Now = Origin.AddSeconds(index + 1);
            secondTime.Now = Origin.AddSeconds(index + 3 + index % 3);
            first.Add(Fragment(index, index + 1));
            // Another stream can have a completely different relative media origin.
            second.Add(Fragment(index + 100, index + 1));
        }
        var firstRange = first.Snapshot(1, "live", "").Ranges.Single();
        var secondRange = second.Snapshot(2, "live", "").Ranges.Single();
        Check(firstRange.Start == secondRange.Start && firstRange.End == secondRange.End,
            "Identical footage stays aligned across different startup, delivery delays and media origins");
        for (var secondIndex = 0; secondIndex < 20; secondIndex++)
        {
            var at = Origin.AddSeconds(secondIndex + 0.5).ToUnixTimeMilliseconds();
            var a = first.Playlist(firstRange.Epoch, at)!.Fragments.Single(item => item.Start <= at && at < item.End);
            var b = second.Playlist(secondRange.Epoch, at)!.Fragments.Single(item => item.Start <= at && at < item.End);
            Check(Math.Abs(MediaAt(a, at) - (MediaAt(b, at) - 100)) < 0.001,
                "A shared historical instant selects the same source frame in independently started cameras");
        }
        // Initial source flushing may itself be delayed; later low-latency samples calibrate startup.
        var startup = Buffer(firstTime);
        startup.Add(Fragment(0, 3));
        startup.Add(Fragment(1, 3.2));
        startup.Add(Fragment(2, 3.4));
        startup.Add(Fragment(3, 4));
        var range = startup.Snapshot(1, "", "").Ranges.Single();
        Check(range.Start == Origin.ToUnixTimeMilliseconds() && range.End == Origin.AddSeconds(4).ToUnixTimeMilliseconds(),
            "Lower source flush latency removes the first-fragment startup offset from retained startup media");
    }

    private static void Pacing()
    {
        foreach (var rate in new[] { 0.95, 1.02, 1.2 })
        {
            var time = new TestTime();
            var buffer = Buffer(time);
            for (var index = 0; index < 1800; index++)
            {
                time.Now = Origin.AddSeconds((index + 1) * rate);
                buffer.Add(Fragment(index, (index + 1) * rate));
            }
            var range = buffer.Snapshot(1, "", "").Ranges.Single();
            var last = buffer.Playlist(range.Epoch, range.End - 1)!.Fragments.Last();
            Check(Math.Abs(range.End - time.Now.ToUnixTimeMilliseconds()) <= 1 && Math.Abs(last.ClockRate - rate) < 0.000001,
                $"Source pace {rate} does not accumulate drift over thirty minutes of media");
            var at = last.Start + (last.End - last.Start) / 2;
            Check(Math.Abs(MediaAt(last, at) - (last.MediaStartSeconds + 0.5)) < 0.002,
                "Wall time converts back to the source media clock using the measured pace");
        }
    }

    private static void PausesAndJitter()
    {
        var time = new TestTime { Now = Origin.AddSeconds(20) };
        var buffer = Buffer(time);
        for (var index = 0; index < 6; index++)
        {
            buffer.Add(Fragment(index, index + 1));
        }
        var before = buffer.Snapshot(1, "", "").Ranges.Single();
        var pausedFrame = buffer.Playlist(before.Epoch, Origin.AddSeconds(2.5).ToUnixTimeMilliseconds())!.Fragments[0];
        buffer.Add(Fragment(6, 11));
        buffer.Add(Fragment(7, 12));
        var ranges = buffer.Snapshot(1, "", "").Ranges;
        Check(ranges.Length == 2 && ranges[0].End == Origin.AddSeconds(6).ToUnixTimeMilliseconds() &&
            ranges[1].Start == Origin.AddSeconds(10).ToUnixTimeMilliseconds() && ranges[1].End == Origin.AddSeconds(12).ToUnixTimeMilliseconds(),
            "A four-second source pause exposes a real coverage gap instead of accumulating a stale timestamp origin");
        Check(buffer.Playlist(before.Epoch, Origin.AddSeconds(8).ToUnixTimeMilliseconds()) is null &&
            buffer.Playlist(before.Epoch, Origin.AddSeconds(2.5).ToUnixTimeMilliseconds())!.Fragments[0] == pausedFrame,
            "Gap seeks do not invent pictures and established paused history is not retimed");

        var jitter = Buffer(time);
        var delays = new[] { 0.0, 0, 0, 0, 0.9, 0, 2, 1, 0, 0, 0, 0 };
        for (var index = 0; index < delays.Length; index++)
        {
            jitter.Add(Fragment(index, index + 1 + delays[index]));
        }
        var continuous = jitter.Snapshot(1, "", "").Ranges.Single();
        Check(continuous.Start == Origin.ToUnixTimeMilliseconds() && continuous.End == Origin.AddSeconds(delays.Length).ToUnixTimeMilliseconds(),
            "Isolated delayed flushing and burst catch-up neither introduce gaps nor shift the established timeline");
    }

    private static void PresentationAndRecovery()
    {
        var time = new TestTime { Now = Origin.AddSeconds(10) };
        var buffer = Buffer(time);
        buffer.Add(Fragment(0, 1.2) with
        {
            PresentationEndSeconds = 1.2
        });
        var first = buffer.Snapshot(1, "", "").Ranges.Single();
        Check(first.Start == Origin.ToUnixTimeMilliseconds(), "B-frame presentation end calibrates the decode clock without adding a composition offset to wall time");
        foreach (var invalid in new[]
        {
            Fragment(double.NaN, 2), Fragment(-1, 2), Fragment(2, 2) with { DurationSeconds = 0 },
            Fragment(2, 2) with { DurationSeconds = double.NaN }, Fragment(2, 2) with { DurationSeconds = 31 },
            Fragment(2, 2) with { PresentationEndSeconds = double.PositiveInfinity },
            Fragment(2, 2) with { PresentationEndSeconds = 2 }, Fragment(0, 0)
        })
        {
            Reject(() => buffer.Add(invalid));
        }
        Check(buffer.Snapshot(1, "", "").Ranges.Single() == first, "Malformed timestamps leave valid retained media intact");
        buffer.Initialize(new([2], "video/mp4", false));
        buffer.Add(Fragment(0, 5));
        var epochs = buffer.Snapshot(1, "", "").Ranges;
        Check(epochs.Length == 2 && epochs[0].Epoch != epochs[1].Epoch && epochs[1].Start == Origin.AddSeconds(4).ToUnixTimeMilliseconds(),
            "Reconnects reset timing calibration while preserving independent older epochs");

        var dependencies = Buffer(time);
        dependencies.Add(Fragment(0, 1) with
        {
            StartsWithKeyframe = false
        });
        Check(dependencies.Snapshot(1, "", "").Ranges.Length == 0, "Undecodable startup interframes do not establish a timing origin");
        for (var index = 1; index < 12; index++)
        {
            dependencies.Add(Fragment(index, index + 1) with
            {
                StartsWithKeyframe = index == 1 || index == 8
            });
        }
        var range = dependencies.Snapshot(1, "", "").Ranges.Single();
        var window = dependencies.Playlist(range.Epoch, Origin.AddSeconds(7.5).ToUnixTimeMilliseconds())!;
        Check(window.Fragments[0].MediaStartSeconds == 1 && window.Fragments.Any(item => item.MediaStartSeconds == 7),
            "Long keyframe intervals retain decode dependencies while the seek targets the exact source time");
        Reject(() => dependencies.Add(Fragment(0, 13)));
    }

    private static void JitterCoverage()
    {
        foreach (var milliseconds in new[] { 1, 27, 73, 100, 101, 120, 158, 199, 200, 201 })
        {
            foreach (var mediaOrigin in new[] { 0, 100 })
            {
                var time = new TestTime { Now = Origin.AddSeconds(20) };
                var buffer = Buffer(time);
                for (var index = 0; index < 8; index++)
                {
                    buffer.Add(Fragment(mediaOrigin + index, index + 1 + (index >= 4 ? milliseconds / 1000.0 : 0))
                        with
                    {
                        StartsWithKeyframe = index % 2 == 0
                    });
                }
                var snapshot = buffer.Snapshot(1, "live", "");
                var at = Origin.AddMilliseconds(4000 + milliseconds / 2).ToUnixTimeMilliseconds();
                var playlist = buffer.Playlist(snapshot.Ranges[0].Epoch, at);
                if (milliseconds > 200)
                {
                    Check(snapshot.Ranges.Length == 2 && playlist is null,
                        "Timing discontinuities beyond both neighboring cadence uncertainties remain missing footage");
                    continue;
                }
                Check(snapshot.Ranges.Length == 1 && playlist is not null,
                    "Every advertised small timing gap selects retained compressed footage instead of expiring");
                var selected = playlist!.Fragments.Single(fragment => fragment.Start <= at && at < fragment.End);
                var media = MediaAt(selected, at);
                Check(media >= selected.MediaStartSeconds && media < selected.MediaStartSeconds + selected.DurationSeconds &&
                    selected.End == Origin.AddMilliseconds(4000 + milliseconds).ToUnixTimeMilliseconds(),
                    "Jitter coverage maps inside an available media fragment without seeking beyond its decoded end");
                foreach (var range in snapshot.Ranges)
                {
                    for (var position = range.Start; position < range.End; position++)
                    {
                        if (buffer.Playlist(range.Epoch, position) is null)
                        {
                            throw new Exception("Advertised rewind coverage returned expired footage at a timing boundary.");
                        }
                    }
                }
                Check(buffer.Playlist(snapshot.Ranges[0].Epoch, snapshot.Ranges[0].Start - 1) is null &&
                    buffer.Playlist(snapshot.Ranges[0].Epoch, snapshot.Ranges[0].End) is null,
                    "Jitter coverage cannot substitute media outside the retained range");
                var clip = buffer.Select(at, at + 200).Single();
                Check(Math.Abs(clip.TrimSeconds - (media - playlist.Fragments[0].MediaStartSeconds)) < 0.000001 &&
                    clip.TrimSeconds >= 0 && clip.TrimSeconds < clip.Fragments.Length && double.IsFinite(clip.ClockRate) && clip.ClockRate > 0,
                    "Selections inside the same jitter coverage remain bounded to playable export media");
            }
        }
    }

    private static void BrokenDecodeCoverage()
    {
        var buffer = Buffer(new TestTime { Now = Origin.AddSeconds(10) });
        buffer.Add(Fragment(0, 1));
        buffer.Add(Fragment(1.05, 2.05) with
        {
            StartsWithKeyframe = false
        });
        buffer.Add(Fragment(2.05, 3.05) with
        {
            StartsWithKeyframe = false
        });
        buffer.Add(Fragment(3.05, 4.05));
        var ranges = buffer.Snapshot(1, "", "").Ranges;
        Check(ranges.Length == 2 && ranges[0].End == Origin.AddSeconds(1).ToUnixTimeMilliseconds() &&
            ranges[1].Start == Origin.AddMilliseconds(3050).ToUnixTimeMilliseconds(),
            "Missing compressed media and dependent frames stay unavailable until the next independent keyframe: " +
            string.Join(", ", ranges.Select(range => $"{range.Start - Origin.ToUnixTimeMilliseconds()}–{range.End - Origin.ToUnixTimeMilliseconds()}")));
        foreach (var range in ranges)
        {
            Check(buffer.Playlist(range.Epoch, range.Start) is not null && buffer.Playlist(range.Epoch, range.End - 1) is not null,
                "Coverage advertises only complete decode chains even when missing media is within timing tolerance");
        }
    }

    private static void CadenceScaledCoverage()
    {
        foreach (var duration in new[] { 0.25, 1, 2 })
        {
            foreach (var rate in new[] { 0.8, 1, 1.2 })
            {
                foreach (var withinUncertainty in new[] { false, true })
                {
                    var buffer = Buffer(new TestTime { Now = Origin.AddMinutes(1) });
                    var uncertainty = Math.Max(0.1, duration * rate * 2 * FragmentTimeline.MaximumRelativeIntervalVariation);
                    var gap = uncertainty * (withinUncertainty ? 0.75 : 1.25);
                    for (var index = 0; index < 8; index++)
                    {
                        buffer.Add(new([1], duration, index * duration, index % 4 == 0)
                        {
                            ProducedAt = Origin.AddSeconds((index + 1) * duration * rate + (index >= 4 ? gap : 0))
                        });
                    }
                    var ranges = buffer.Snapshot(1, "live", "").Ranges;
                    Check(ranges.Length == (withinUncertainty ? 1 : 2),
                        $"Boundary uncertainty follows {duration}s fragments at source pace {rate}, preserving larger gaps");
                    var at = withinUncertainty ? ranges[0].Start + (ranges[0].End - ranges[0].Start) / 2
                        : ranges[0].End + (ranges[1].Start - ranges[0].End) / 2;
                    Check((buffer.Window(ranges[0].Epoch, at) is not null) == withinUncertainty,
                        "Seek availability matches cadence-scaled coverage, including measured-clock calibration shifts");
                }
            }
        }
    }

    private static void CapturedJitterCoverage()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "live-sync-variable-fragments.json"));
        var traces = JsonSerializer.Deserialize<TimingTrace[]>(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var trace in traces)
        {
            var buffer = Buffer(new TestTime { Now = Origin.AddMinutes(1) });
            foreach (var packet in trace.Packets)
            {
                buffer.Add(new([1], packet.End - packet.Start, packet.Start, packet.End - packet.Start > 0.9)
                {
                    ProducedAt = Origin.AddMilliseconds(packet.Produced)
                });
            }
            var snapshot = buffer.Snapshot(1, "", "");
            foreach (var range in snapshot.Ranges)
            {
                for (var at = range.Start; at < range.End; at++)
                {
                    var playlist = buffer.Playlist(range.Epoch, at);
                    if (playlist is null || !playlist.Fragments.Any(fragment => fragment.Start <= at && at < fragment.End))
                    {
                        throw new Exception("A retained camera timing trace advertised unavailable or unmapped rewind footage.");
                    }
                }
            }
            Check(snapshot.Ranges.Length > 0, "Real variable-duration camera timing traces retain playable coverage at every advertised millisecond");
        }
    }

    private static async Task ConcurrentReadersAsync()
    {
        var time = new TestTime { Now = Origin.AddMinutes(10) };
        var buffer = Buffer(time);
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                buffer.Add(Fragment(index, index + 1));
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var index = 0; index < 300; index++)
            {
                foreach (var range in buffer.Snapshot(1, "", "").Ranges)
                {
                    if (range.Start >= range.End || buffer.Playlist(range.Epoch, range.Start) is null)
                    {
                        throw new Exception("Concurrent timing refinement exposed incomplete metadata.");
                    }
                }
            }
        }));
        await Task.WhenAll(readers.Append(writer));
        Check(buffer.Snapshot(1, "", "").Ranges.Single().End == Origin.AddSeconds(500).ToUnixTimeMilliseconds(),
            "Concurrent readers observe coherent fragment timing while capture refines the neighboring fragment");
    }

    private static void CapturedNucRewindCoverage()
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewind-production-jitter.json"));
        var trace = JsonSerializer.Deserialize<TimingTrace>(fixture, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var buffer = Buffer(new TestTime { Now = Origin.AddMinutes(1) });
        for (var index = 0; index < trace.Packets.Length; index++)
        {
            var packet = trace.Packets[index];
            buffer.Add(new([1], packet.End - packet.Start, packet.Start, index % 5 == 0)
            {
                ProducedAt = Origin.AddMilliseconds(packet.Produced)
            });
        }
        var snapshot = buffer.Snapshot(1, "live", "");
        Check(snapshot.Ranges.Length == 1,
            "Uninterrupted camera-host production jitter does not split retained media into false missing-footage ranges: " + snapshot.Ranges.Length);
        var range = snapshot.Ranges.Single();
        for (var at = range.End - 30000; at < range.End - 20000; at += 10)
        {
            var window = buffer.Window(range.Epoch, at);
            if (window is null || !window.Playlist.Fragments.Any(fragment => fragment.Start <= at && at < fragment.End))
            {
                throw new Exception("Retained footage 20–30 seconds behind live became unavailable.");
            }
        }
        Check(buffer.Select(range.End - 30000, range.End - 20000).Length == 1,
            "A replay export across captured jitter retains the same continuous playable footage as rewind");
    }

    private static void RecentRewindAcrossTimingSeams()
    {
        var buffer = Buffer(new TestTime { Now = Origin.AddSeconds(75) });
        for (var index = 0; index < 70; index++)
        {
            // Model the 100–150 ms UTC mapping seams observed on camera-host while
            // encoded timestamps and decode dependencies remain uninterrupted.
            buffer.Add(Fragment(index, index + 1 + index / 5 * 0.12) with
            {
                StartsWithKeyframe = index % 5 == 0
            });
        }
        var snapshot = buffer.Snapshot(1, "live", "");
        Check(snapshot.Ranges.Length == 1,
            "Cadence-scale timing jitter does not report missing footage twenty to thirty seconds behind live: " + snapshot.Ranges.Length);
        var range = snapshot.Ranges.Single();
        for (var at = range.End - 30000; at < range.End - 20000; at += 10)
        {
            var window = buffer.Window(range.Epoch, at);
            if (window is null || !window.Playlist.Fragments.Any(fragment => fragment.Start <= at && at < fragment.End))
            {
                throw new Exception("An unbroken decode chain was rejected at a recent rewind timing seam.");
            }
        }
        Check(buffer.Select(range.End - 30000, range.End - 20000).Length == 1,
            "Saving the same recent replay does not split a continuous decode chain at timing jitter");
    }

    private static void ExportsAndRetention()
    {
        foreach (var rate in new[] { 0.8, 1.0, 1.2 })
        {
            var time = new TestTime();
            var buffer = Buffer(time);
            for (var index = 0; index < 12; index++)
            {
                time.Now = Origin.AddMilliseconds(Math.Round((index + 1) * rate * 1000));
                buffer.Add(Fragment(index, (index + 1) * rate));
            }
            var clip = buffer.Select(Origin.AddMilliseconds(2350 * rate).ToUnixTimeMilliseconds(),
                Origin.AddMilliseconds(4450 * rate).ToUnixTimeMilliseconds()).Single();
            Check(Math.Abs(clip.TrimSeconds - 0.35) < 0.000001 && Math.Abs(clip.ClockRate - rate) < 0.000001 &&
                Math.Abs(clip.DurationSeconds - 2.1 * rate) < 0.001,
                $"Replay export at source pace {rate} trims media time and retains the selected wall duration");

            var retained = new LiveRewindBuffer(TimeSpan.FromSeconds(4), 8, time);
            retained.Initialize(new([0], "video/mp4", false));
            for (var index = 0; index < 12; index++)
            {
                time.Now = Origin.AddMilliseconds(Math.Round((index + 1) * rate * 1000));
                retained.Add(Fragment(index, (index + 1) * rate));
            }
            var snapshot = retained.Snapshot(1, "", "");
            Check(snapshot.Bytes <= 8 && snapshot.Ranges.Single().End == time.Now.ToUnixTimeMilliseconds() &&
                snapshot.Ranges.Single().Start > time.Now.AddSeconds(-4).ToUnixTimeMilliseconds() - rate * 1000,
                "Calibrated sources still obey memory and wall-clock retention budgets");
            time.Now = time.Now.AddSeconds(10);
            Check(retained.Snapshot(1, "", "").Ranges.Length == 0,
                "Expired calibrated footage releases its timing and compressed media");
        }
        var gapTime = new TestTime { Now = Origin.AddSeconds(12) };
        var gap = Buffer(gapTime);
        for (var index = 0; index < 6; index++)
        {
            gap.Add(Fragment(index, index + 1) with
            {
                StartsWithKeyframe = index == 0
            });
        }
        gap.Add(Fragment(6, 11) with
        {
            StartsWithKeyframe = false
        });
        gap.Add(Fragment(7, 12) with
        {
            StartsWithKeyframe = false
        });
        var clips = gap.Select(Origin.ToUnixTimeMilliseconds(), Origin.AddSeconds(12).ToUnixTimeMilliseconds());
        Check(clips.Length == 2 && clips[0].EndedAt == Origin.AddSeconds(6) && clips[1].StartedAt == Origin.AddSeconds(10) &&
            clips[1].TrimSeconds == 6 && clips[1].Fragments.Length == 8,
            "A paused source can export its later interframes using the earlier keyframe without filling the wall-time gap");
        var lost = Buffer(gapTime);
        lost.Add(Fragment(0, 1));
        lost.Add(Fragment(3, 4) with
        {
            StartsWithKeyframe = false
        });
        var lostEpoch = lost.Snapshot(1, "", "").Ranges[0].Epoch;
        Check(lost.Playlist(lostEpoch, Origin.AddSeconds(3.5).ToUnixTimeMilliseconds()) is null,
            "Missing compressed media cannot borrow an earlier keyframe across a broken decode chain");
        lost.Add(Fragment(4, 5));
        Check(lost.Playlist(lostEpoch, Origin.AddSeconds(4.5).ToUnixTimeMilliseconds())!.Fragments[0].MediaStartSeconds == 4,
            "A new keyframe restores playback after missing compressed media");
    }

    private static LiveRewindBuffer Buffer(TestTime time)
    {
        var buffer = new LiveRewindBuffer(TimeSpan.FromDays(1), 1024 * 1024, time);
        buffer.Initialize(new([0], "video/mp4", false));
        return buffer;
    }

    private static VideoFragment Fragment(double start, double producedSeconds) =>
        new([1], 1, start)
        {
            ProducedAt = Origin.AddMilliseconds(Math.Round(producedSeconds * 1000))
        };

    private static double MediaAt(RewindFragment fragment, long at) =>
        fragment.MediaStartSeconds + (at - fragment.Start) / (fragment.ClockRate * 1000);

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new Exception("Invalid synchronization metadata was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Origin;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record TimingTrace(TimingPacket[] Packets);
    private sealed record TimingPacket(double Start, double End, double Produced);
}
