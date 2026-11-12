using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class RewindRecordingTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "rewind-save-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            SelectionTests();
            await AvailableFootageImportTestsAsync(directory);
            await ImportTestsAsync(directory);
            await FilterTestsAsync(directory);
            await RendererTestsAsync(directory);
            await RendererFailureTestsAsync(directory);
            await AccessTestsAsync(directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
        Console.WriteLine("All saved replay checks passed.");
    }

    private static void SelectionTests()
    {
        var time = new TestTime();
        var buffer = Buffer(time);
        var range = buffer.Snapshot(1, "", "").Ranges.Single();
        var start = range.Start + 2350;
        var end = range.Start + 6450;
        var clip = buffer.Select(start, end).Single();
        Check(clip.TrimSeconds == 2.35 && clip.DurationSeconds == 4.1 && clip.Fragments.Length == 7,
            "Selections retain the preceding keyframe and trim fractional boundaries");
        var connection = new ViewerSettings { ChannelCount = 2 };
        foreach (var backwards in new[] { false, true })
        {
            var request = new SaveRewindRecordingRequest(1, backwards ? end : start, backwards ? start : end, [1, 2]);
            var selection = RewindRecordingSelection.Capture(request, connection, _ => buffer);
            Check(selection.Cameras.Length == 2 && selection.Cameras.All(camera => camera.Clips.Single().StartedAt.ToUnixTimeMilliseconds() == start),
                "Forward and backward grid selections normalize the same interval for every camera");
        }
        Check(RewindRecordingSelection.Capture(new(1, start, end, [2]), connection, _ => buffer).Cameras.Single().CameraId == "camera-2",
            "Focused selections preserve camera identity");
        foreach (var request in new[]
        {
            new SaveRewindRecordingRequest(1, start, start, [1]),
            new(1, start, end, []), new(1, start, end, [0]), new(1, start, end, [1, 1]),
            new(1, long.MinValue, end, [1]), new(1, start, long.MaxValue, [1]), new(1, start, end, null!)
        })
        {
            Reject(request.Validate);
        }
        Reject(() => RewindRecordingSelection.Capture(new(1, start, end, [3]), connection, _ => buffer));
        var empty = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000, time);
        empty.Initialize(new([0], "video/mp4", false));
        foreach (var missing in new LiveRewindBuffer?[] { null, empty })
        {
            foreach (var availableChannel in new[] { 1, 2 })
            {
                foreach (var backwards in new[] { false, true })
                {
                    var request = new SaveRewindRecordingRequest(1, backwards ? end : start, backwards ? start : end, [1, 2]);
                    var selection = RewindRecordingSelection.Capture(request, connection,
                        channel => channel == availableChannel ? buffer : missing);
                    Check(selection.Cameras.Single().Channel == availableChannel &&
                        selection.Cameras[0].Clips.Single().StartedAt.ToUnixTimeMilliseconds() == start,
                        "Grid saves skip missing cameras regardless of order or selection direction");
                }
            }
            Reject(() => RewindRecordingSelection.Capture(new(1, start, end, [1, 2]), connection, _ => missing));
            Reject(() => RewindRecordingSelection.Capture(new(1, start, end, [2]), connection,
                channel => channel == 1 ? buffer : missing));
            Reject(() => RewindRecordingSelection.Capture(new(1, start, end, [1, 3]), connection, _ => missing));
        }
        var cropped = buffer.Select(range.Start - 1000, range.End + 1000).Single();
        Check(cropped.StartedAt.ToUnixTimeMilliseconds() == range.Start &&
            cropped.EndedAt.ToUnixTimeMilliseconds() == range.End && cropped.TrimSeconds == 0 && cropped.DurationSeconds == 8,
            "Missing footage at both endpoints is cropped to the available range");
        Check(buffer.Select(range.Start - 1000, range.Start).Length == 0,
            "Intervals ending at the first frame contain no footage");
        Reject(() => RewindRecordingSelection.Capture(new(1, range.End, range.End + 1000, [1, 2]), connection, _ => buffer));
        Check(buffer.Select(range.End, range.End + 1000).Length == 0, "Unavailable intervals never substitute live footage");

        time.Advance(5);
        buffer.Initialize(new([9], "video/mp4", false));
        buffer.Add(new([10], 1) { ProducedAt = time.GetUtcNow() });
        var ranges = buffer.Snapshot(1, "", "").Ranges;
        var clips = buffer.Select(start, ranges.Last().End);
        Check(clips.Length == 2 && clips[0].EndedAt.ToUnixTimeMilliseconds() == range.End &&
            clips[1].Initialization[0] == 9 && clips[1].StartedAt > clips[0].EndedAt,
            "Reconnects become independent clips with correct initialization and wall-clock gaps");
        time.Advance(53);
        var retained = buffer.Snapshot(1, "", "").Ranges;
        Check(retained[0].Start > range.Start && buffer.Select(range.Start, retained.Last().End).First().StartedAt.ToUnixTimeMilliseconds() == retained[0].Start,
            "A start that expires during end selection still saves the remaining retained footage");
        time.Advance(100);
        Check(buffer.Snapshot(1, "", "").Ranges.Length == 0 && clip.Fragments.Length == 7 && clip.Initialization[0] == 0,
            "Admitted media snapshots survive pruning without copying video bytes");

        var gappedTime = new TestTime();
        var gapped = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000, gappedTime);
        gapped.Initialize(new([0], "video/mp4", false));
        gapped.Add(new([1], 1, 0) { ProducedAt = gappedTime.GetUtcNow() });
        gapped.Add(new([2], 1, 3) { ProducedAt = gappedTime.GetUtcNow().AddSeconds(3) });
        var gaps = gapped.Snapshot(1, "", "").Ranges;
        var gapClips = gapped.Select(gaps[0].Start - 1000, gaps[1].End + 1000);
        Check(gapClips.Length == 2 && gapClips.All(item => item.DurationSeconds == 1) &&
            gapClips[0].EndedAt.ToUnixTimeMilliseconds() == gaps[0].End &&
            gapClips[1].StartedAt.ToUnixTimeMilliseconds() == gaps[1].Start,
            "Missing timestamps within one epoch do not stretch saved footage across a gap");
        Check(gapped.Select(gaps[0].End, gaps[1].Start).Length == 0 &&
            gapped.Select(gaps[0].End, gaps[1].End).Single().StartedAt.ToUnixTimeMilliseconds() == gaps[1].Start &&
            gapped.Select(gaps[0].Start, gaps[1].Start).Single().EndedAt.ToUnixTimeMilliseconds() == gaps[0].End,
            "Endpoints in gaps save only overlapping captured footage");
        gapped.Add(new([3], 1, 6, false) { ProducedAt = gappedTime.GetUtcNow().AddSeconds(6) });
        Check(gapped.Select(gaps[0].Start + 6000, gaps[0].Start + 7000).Length == 0,
            "A selected interval containing only undecodable interframes has no exportable footage");
        gapped.Add(new([4], 1, 7, true) { ProducedAt = gappedTime.GetUtcNow().AddSeconds(7) });
        var restored = gapped.Select(gaps[0].Start, gaps[0].Start + 8000);
        Check(restored.Length == 3 && restored.Last().StartedAt.ToUnixTimeMilliseconds() == gaps[0].Start + 7000,
            "Exports skip undecodable interframes after a gap and retain the next keyframe");
    }

    private static LiveRewindBuffer Buffer(TestTime time)
    {
        var buffer = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000, time);
        buffer.Initialize(new([0], "video/mp4", true));
        for (var index = 0; index < 8; index++)
        {
            buffer.Add(new([(byte)(index + 1)], 1, index, index % 3 == 0) { ProducedAt = time.GetUtcNow() });
            time.Advance(1);
        }
        return buffer;
    }

    private static async Task AvailableFootageImportTestsAsync(string directory)
    {
        var time = new TestTime();
        var continuous = Buffer(time);
        var range = continuous.Snapshot(1, "", "").Ranges.Single();
        var intermittent = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000, time);
        intermittent.Initialize(new([9], "video/mp4", false));
        // This camera begins eight seconds after the continuous camera, then wakes again after a gap.
        intermittent.Add(new([10], 1, 0) { ProducedAt = time.GetUtcNow() });
        intermittent.Add(new([11], 1, 3) { ProducedAt = time.GetUtcNow().AddSeconds(3) });
        var empty = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000, time);
        var connection = new ViewerSettings { ChannelCount = 4 };
        foreach (var backwards in new[] { false, true })
        {
            var start = range.Start - 1000;
            var end = range.End + 5000;
            var selection = RewindRecordingSelection.Capture(new(1, backwards ? end : start, backwards ? start : end, [1, 2, 3, 4]),
                connection, channel => channel == 1 ? continuous : channel == 2 ? intermittent : channel == 3 ? empty : null);
            var renderer = new TestRenderer();
            var store = new FileRecordingStore(new()
            {
                Directory = Path.Combine(directory, "available-" + backwards),
                Enabled = false
            }, new("missing"));
            var saved = await new RewindRecordingExporter(renderer).SaveAsync(selection, store, CancellationToken.None);
            Check(saved.Length == 3 && renderer.Calls == 3 && saved.Count(item => item.Channel == 1) == 1 &&
                saved.Count(item => item.Channel == 2) == 2 && saved.All(item => item.Status == "ready" && item.Trigger.Source == "replay"),
                "Available grid footage imports successfully while empty cameras are skipped");
            var bursts = saved.Where(item => item.Channel == 2).OrderBy(item => item.StartedAt).ToArray();
            Check(bursts.All(item => item.DurationSeconds == 1) && bursts[1].StartedAt > bursts[0].EndedAt &&
                saved.Single(item => item.Channel == 1).DurationSeconds == 8 &&
                store.Search(new()
                {
                    SavedReplayOnly = true
                }).Length == 3,
                "Separate motion-camera bursts remain searchable with actual times and no saved gap");
        }
    }

    private static async Task ImportTestsAsync(string directory)
    {
        var renderer = new TestRenderer();
        var exporter = new RewindRecordingExporter(renderer);
        var settings = new RecordingSettings { Directory = Path.Combine(directory, "library"), Enabled = false };
        var store = new FileRecordingStore(settings, new("missing-ffmpeg"));
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var clip = new RecordingClip(start, start.AddSeconds(2), 0, [0], [[1]]);
        var selection = new RewindRecordingSelection("default", [new(1, "camera-stable", "Front", [clip]), new(2, "camera-2", "Back", [clip])]);
        var saved = await exporter.SaveAsync(selection, store, CancellationToken.None);
        Check(saved.Length == 2 && store.Count == 2 && saved.All(item => item.Status == "ready" && item.Trigger.Source == "replay" &&
            item.Trigger.Reason == "Saved live replay" && item.DurationSeconds == 2 && store.MediaPath(item.Id) is not null),
            "Manual saves become playable ordinary recordings while automatic capture remains disabled");
        Check(saved[0].CameraId == "camera-stable" && saved[0].StartedAt == start &&
            store.UsedBytes == saved.Sum(item => item.SizeBytes),
            "Camera identity, historical timestamps and storage accounting survive import");
        var restarted = new FileRecordingStore(settings, new("missing-ffmpeg"));
        Check(restarted.Count == 2 && restarted.Search(new()
        {
            SavedReplayOnly = true
        }).Length == 2 &&
            restarted.Timeline(1, start.AddSeconds(-1), start.AddSeconds(3), true).Recordings.Single().Id == saved[0].Id,
            "Replay metadata and filtered searches and timelines survive a server restart");
        Check(new FileRecordingStore(settings with
        {
            ConfigurationId = "other"
        }, new("missing")).Count == 0,
            "Imports remain isolated by configuration");
        await store.ProtectAsync(saved[0].Id, true, CancellationToken.None);
        Check(!await store.DeleteAsync(saved[0].Id, CancellationToken.None), "Saved replays use ordinary recording protection");
        await store.ProtectAsync(saved[0].Id, false, CancellationToken.None);
        Check(await store.DeleteAsync(saved[0].Id, CancellationToken.None), "Saved replays use ordinary recording deletion");

        renderer.FailAt = renderer.Calls + 2;
        await RejectAsync(() => exporter.SaveAsync(selection, store, CancellationToken.None));
        Check(store.Count == 1 && renderer.Paths.All(path => !Directory.Exists(Path.GetDirectoryName(path))),
            "A conversion failure leaves no partial batch or temporary files");
        renderer.FailAt = null;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await RejectAsync(() => exporter.SaveAsync(selection, store, cancelled.Token));
        Check(store.Count == 1, "Cancellation before admission leaves the recording library unchanged");

        var input = Path.Combine(directory, "prepared.mp4");
        await File.WriteAllBytesAsync(input, [1, 2, 3]);
        var first = saved[0] with
        {
            Id = Guid.NewGuid().ToString("N")
        };
        var second = saved[1] with
        {
            Id = Guid.NewGuid().ToString("N")
        };
        var layout = new RecordingFileLayout(settings.Directory);
        var collision = second with
        {
            FileBasePath = layout.CreateBasePath(second)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(layout.PathFor(collision, ".mp4"))!);
        // Force the second file admission to fail after the first catalogue row has been saved.
        Directory.CreateDirectory(layout.PathFor(collision, ".mp4"));
        await RejectAsync(() => store.ImportAsync([new(first, input), new(second, input)], CancellationToken.None));
        Check(store.Find(first.Id) is null && store.Count == 1 && !File.Exists(layout.PathFor(first with
        {
            FileBasePath = layout.CreateBasePath(first)
        }, ".mp4")),
            "Failure in a later batch import rolls back earlier files and catalogue entries");
        await RejectAsync(() => store.ImportAsync([new(first with { ConfigurationId = "other" }, input)], CancellationToken.None));
        await RejectAsync(() => store.ImportAsync([new(first, input), new(first, input)], CancellationToken.None));

        var tiny = new FileRecordingStore(settings with
        {
            Directory = Path.Combine(directory, "quota"),
            MaximumStorageGigabytes = .000000001
        }, new("missing"));
        await RejectAsync(() => tiny.ImportAsync([new(first, input)], CancellationToken.None));
        Check(tiny.Count == 0, "Imports obey the configured recording storage budget");
        await File.WriteAllBytesAsync(input, []);
        await RejectAsync(() => store.ImportAsync([new(first, input)], CancellationToken.None));
        Check(store.Count == 1, "Empty rendered videos are rejected");
    }

    private static async Task FilterTestsAsync(string directory)
    {
        var library = Path.Combine(directory, "filters");
        var catalog = new SqliteRecordingCatalog(library, "default");
        catalog.Initialize();
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 60; index++)
        {
            catalog.Save(new RecordingEvent
            {
                Channel = 1,
                TriggeredAt = now.AddSeconds(index),
                StartedAt = now.AddSeconds(index),
                EndedAt = now.AddSeconds(index + 1),
                Status = "ready",
                FileBasePath = "unused/" + index,
                Trigger = new(index % 2 == 0 ? "replay" : "motion", "Test")
            });
        }
        Check(catalog.Search(new()
        {
            SavedReplayOnly = true,
            Limit = 5
        }).Length == 5 &&
            catalog.Search(new()
            {
                SavedReplayOnly = true,
                Since = now.AddSeconds(10),
                Before = now.AddSeconds(20)
            }).Length == 5 &&
            catalog.Search(new()
            {
                SavedReplayOnly = true,
                Channel = 2
            }).Length == 0 &&
            catalog.Timeline(1, now, now.AddMinutes(1), savedReplayOnly: true).Recordings.Length == 30,
            "Replay filtering combines with camera, dates, paging and coverage before results are returned");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "filter-settings.json")
        }).Build();
        using var settings = new JsonAppSettingsStore(configuration);
        var filters = new RecordingFilterStore(settings);
        var saved = await filters.SaveAsync("default", new(null, "Saved replays", new()
        {
            SavedReplayOnly = true
        }));
        using var reloaded = new JsonAppSettingsStore(configuration);
        Check((await new RecordingFilterStore(reloaded).ListAsync("default")).Single().Filter.SavedReplayOnly,
            "Named filters preserve the replay-only selection across reloads");
        var replaced = await filters.SaveAsync("default", new(saved.Id, null, new()));
        Check(!replaced.Filter.SavedReplayOnly, "Replacing a named filter can clear the replay-only selection");
    }

    private static async Task RendererTestsAsync(string directory)
    {
        var ffmpeg = new FfmpegProcess(Environment.GetEnvironmentVariable("SCV_TEST_FFMPEG") ?? "ffmpeg");
        foreach (var audio in new[] { false, true })
        {
            var source = Path.Combine(directory, "source-" + audio + ".mp4");
            var arguments = new List<string> { "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10" };
            if (audio)
            {
                arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-c:a", "aac"]);
            }
            arguments.AddRange(["-t", "8", "-c:v", "libx264", "-threads", "1", "-bf", "0", "-g", "10",
                "-movflags", "frag_keyframe+empty_moov+default_base_moof", source]);
            await ffmpeg.FileAsync(arguments, CancellationToken.None);
            var time = new TestTime();
            var buffer = new LiveRewindBuffer(TimeSpan.FromMinutes(1), 10000000, time);
            var captureOrigin = time.GetUtcNow();
            await using (var stream = File.OpenRead(source))
            {
                await FragmentedMp4.ReadAsync(stream, initialization =>
                {
                    buffer.Initialize(new(initialization, "video/mp4", audio));
                    return Task.CompletedTask;
                }, fragment =>
                {
                    buffer.Add(fragment with { ProducedAt = captureOrigin.AddMilliseconds(Math.Round(
                        (fragment.PresentationEndSeconds ?? fragment.StartSeconds + fragment.DurationSeconds) * 1000)) });
                    time.Advance(1);
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            var range = buffer.Snapshot(1, "", "").Ranges.Single();
            // Start after multiple keyframes: decode timestamps in the captured input are nonzero.
            var clip = buffer.Select(range.Start + 2350, range.Start + 4450).Single();
            var destination = Path.Combine(directory, "trimmed-" + audio + ".mp4");
            await new FfmpegRecordingClipRenderer(ffmpeg).RenderAsync(clip, destination, CancellationToken.None);
            await ffmpeg.FileAsync(["-i", destination, "-f", "null", "-"], CancellationToken.None);
            var reference = Path.Combine(directory, "reference-" + audio + ".mp4");
            var fullClip = buffer.Select(range.Start, range.End).Single() with
            {
                StartedAt = clip.StartedAt,
                EndedAt = clip.EndedAt,
                TrimSeconds = 2.35
            };
            await new FfmpegRecordingClipRenderer(ffmpeg).RenderAsync(fullClip, reference, CancellationToken.None);
            Check(await VideoHashAsync(ffmpeg, destination) == await VideoHashAsync(ffmpeg, reference),
                "Clipping from a retained keyframe selects the same actual video frames as the full source");
            foreach (var rate in new[] { 0.8, 1.2 })
            {
                var paced = clip with { ClockRate = rate, EndedAt = clip.StartedAt.AddSeconds(clip.DurationSeconds * rate) };
                var pacedReference = fullClip with { ClockRate = rate, EndedAt = paced.EndedAt };
                var pacedPath = Path.Combine(directory, $"paced-{audio}-{rate}.mp4");
                var referencePath = Path.Combine(directory, $"paced-reference-{audio}-{rate}.mp4");
                await new FfmpegRecordingClipRenderer(ffmpeg).RenderAsync(paced, pacedPath, CancellationToken.None);
                await new FfmpegRecordingClipRenderer(ffmpeg).RenderAsync(pacedReference, referencePath, CancellationToken.None);
                Check(await VideoHashAsync(ffmpeg, pacedPath) == await VideoHashAsync(ffmpeg, referencePath),
                    $"Pace {rate}, audio {audio}: a nonzero retained keyframe selects the same actual frames as the full source");
                using var pacedMetadata = JsonDocument.Parse(await ProbeAsync(pacedPath));
                var pacedDuration = double.Parse(pacedMetadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                Check(Math.Abs(pacedDuration - paced.DurationSeconds) < 0.2 &&
                    pacedMetadata.RootElement.GetProperty("streams").EnumerateArray().Any(item => item.GetProperty("codec_type").GetString() == "audio") == audio,
                    "Paced exports preserve wall duration and audio presence");
            }
            using var metadata = JsonDocument.Parse(await ProbeAsync(destination));
            var root = metadata.RootElement;
            var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            Check(Math.Abs(duration - 2.1) < .15 && streams.Any(item => item.GetProperty("codec_name").GetString() == "h264") &&
                streams.Any(item => item.GetProperty("codec_type").GetString() == "audio") == audio &&
                File.Exists(destination + ".jpg") && !File.Exists(destination + ".input.mp4"),
                "Real FFmpeg clips trim non-keyframe boundaries, retain optional audio, and clean temporary input");
        }
    }

    private static async Task RendererFailureTestsAsync(string directory)
    {
        var now = DateTimeOffset.UtcNow;
        var clip = new RecordingClip(now, now.AddSeconds(1), 0, [0], [[1]]);
        var destination = Path.Combine(directory, "failed-render.mp4");
        var failingVideo = new TestFfmpeg { FailAt = 1 };
        await RejectAsync(() => new FfmpegRecordingClipRenderer(failingVideo).RenderAsync(clip, destination, CancellationToken.None));
        Check(!File.Exists(destination + ".input.mp4"), "Conversion failures always release temporary buffered input");
        var failingThumbnail = new TestFfmpeg { FailAt = 2 };
        await new FfmpegRecordingClipRenderer(failingThumbnail).RenderAsync(clip, destination, CancellationToken.None);
        Check(File.Exists(destination) && !File.Exists(destination + ".jpg"),
            "Thumbnail conversion failure preserves a successful playable export");
        var interrupted = new TestFfmpeg { Cancel = true };
        await RejectAsync(() => new FfmpegRecordingClipRenderer(interrupted).RenderAsync(clip, destination, CancellationToken.None));
        Check(!File.Exists(destination + ".input.mp4"), "Cancellation during conversion cleans temporary buffered input");
        foreach (var rate in new[] { 0.0, -1, double.NaN, double.PositiveInfinity, double.Epsilon })
        {
            var invalid = new TestFfmpeg();
            await RejectAsync(() => new FfmpegRecordingClipRenderer(invalid).RenderAsync(clip with { ClockRate = rate }, destination, CancellationToken.None));
            Check(invalid.Calls == 0 && !File.Exists(destination + ".input.mp4"),
                "Invalid source pace does not invoke FFmpeg and releases temporary media");
        }
        foreach (var rate in new[] { 0.25, 4.0 })
        {
            var paced = new TestFfmpeg();
            await new FfmpegRecordingClipRenderer(paced).RenderAsync(clip with { ClockRate = rate }, destination, CancellationToken.None);
            var filters = paced.Arguments[0][Array.IndexOf(paced.Arguments[0], "-af") + 1].Split(',');
            var tempos = filters.Select(value => double.Parse(value[7..], CultureInfo.InvariantCulture)).ToArray();
            Check(tempos.All(value => value is >= 0.5 and <= 2) && Math.Abs(tempos.Aggregate(1.0, (a, b) => a * b) - 1 / rate) < 0.000001,
                "Audio pacing cascades valid FFmpeg tempo filters for very slow and fast sources");
        }
    }

    private static async Task<string> VideoHashAsync(FfmpegProcess ffmpeg, string path)
    {
        using var output = new MemoryStream();
        await ffmpeg.RunAsync(["-i", path, "-map", "0:v:0", "-f", "md5", "pipe:1"], null,
            (stream, token) => stream.CopyToAsync(output, token), CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    private static async Task<string> ProbeAsync(string path)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("SCV_TEST_FFPROBE") ?? "ffprobe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path })
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "FFprobe inspects the saved clip: " + await error);
        return await output;
    }

    private static async Task AccessTestsAsync(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["integrations-directory"] = Path.Combine(directory, "access"),
            ["settings"] = Path.Combine(directory, "access-settings.json")
        }).Build();
        using var settingsStore = new JsonAppSettingsStore(configuration);
        var settings = new IntegrationSettings(settingsStore, configuration);
        var auth = new IntegrationAuth(settings);
        var token = settings.CreateToken("Replay saver", ["trigger"], null);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/playback/recordings";
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer " + token.Value;
        var authorized = false;
        await auth.Authorize(context, _ =>
        {
            authorized = true;
            return Task.CompletedTask;
        });
        Check(authorized, "The trigger scope authorizes explicit replay saves");
        var reader = settings.CreateToken("Reader", ["read"], null);
        context = new DefaultHttpContext();
        context.Request.Path = "/api/playback/recordings";
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = "Bearer " + reader.Value;
        context.Response.Body = new MemoryStream();
        authorized = false;
        await auth.Authorize(context, _ =>
        {
            authorized = true;
            return Task.CompletedTask;
        });
        Check(!authorized && context.Response.StatusCode == 403, "Read-only tokens cannot create replay recordings");
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }
        throw new Exception("Expected invalid selection to fail.");
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            return;
        }
        throw new Exception("Expected save failure.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + description);
        }
        Console.WriteLine("PASS: " + description);
    }

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class TestFfmpeg : IFfmpegProcess
    {
        public int Calls;
        public int? FailAt;
        public bool Cancel;
        public List<string[]> Arguments { get; } = [];

        public async Task RunAsync(IEnumerable<string> arguments, string? input, Func<Stream, CancellationToken, Task> read,
            CancellationToken token, string pipeline = "other", int channel = 0)
        {
            Calls++;
            Arguments.Add(arguments.ToArray());
            if (Cancel)
            {
                throw new OperationCanceledException();
            }
            if (Calls == FailAt)
            {
                throw new IOException("Test media failure.");
            }
            await File.WriteAllBytesAsync(arguments.Last(), [1], token);
        }
    }

    private sealed class TestRenderer : IRecordingClipRenderer
    {
        public int Calls;
        public int? FailAt;
        public List<string> Paths { get; } = [];
        public async Task RenderAsync(RecordingClip clip, string destination, CancellationToken token)
        {
            Paths.Add(destination);
            Calls++;
            if (Calls == FailAt)
            {
                throw new IOException("Test conversion failure.");
            }
            await File.WriteAllBytesAsync(destination, [1, 2, 3], token);
            await File.WriteAllBytesAsync(destination + ".jpg", [4], token);
        }
    }
}
