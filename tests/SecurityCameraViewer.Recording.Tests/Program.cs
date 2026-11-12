using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web.Settings;

static void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception("FAIL: " + name);
    }

    Console.WriteLine("PASS: " + name);
}
static async Task Until(Func<Task<bool>> condition, string name, int seconds = 35)
{
    var clock = Stopwatch.StartNew();
    while (!await condition())
    {
        if (clock.Elapsed.TotalSeconds > seconds)
        {
            throw new Exception("Timed out: " + name);
        }

        await Task.Delay(150);
    }
    Console.WriteLine("PASS: " + name);
}
static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
static Process Start(string executable, string[] arguments, Dictionary<string, string>? environment = null)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
        foreach (var item in environment)
        {
            info.Environment[item.Key] = item.Value;
        }
    }

    var process = Process.Start(info)!;
    process.OutputDataReceived += (_, _) => { };
    process.ErrorDataReceived += (_, _) => { };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}
static async Task Stop(Process? process) { if (process is null) { return; } if (!process.HasExited) { process.Kill(true); } await process.WaitForExitAsync(); process.Dispose(); }

if (args.Contains("--retention-only"))
{
    await RecordingRetentionTests.RunAsync();
    return;
}

if (args.Contains("--mirrors-only"))
{
    await RecordingMirrorTests.RunAsync();
    return;
}

var root = Path.GetFullPath(Path.Combine("artifacts", "recording-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
if (args.Contains("--adaptive-benchmark"))
{
    AdaptiveOptimizationBenchmark.Run(args);
    return;
}
await AdaptiveOptimizationTests.RunAsync();
if (args.Contains("--adaptive-optimization-only"))
{
    return;
}
if (Array.IndexOf(args, "--ai-smoke") is var smokeIndex && smokeIndex >= 0)
{
    await AiDetectionTests.SmokeAsync(args[smokeIndex + 1], args[smokeIndex + 2], args.Contains("--openvino"));
    return;
}

await HotPathTests.RunAsync();
DetectionHistoryTests.Run();
if (args.Contains("--characterization-only"))
{
    return;
}
if (args.Contains("--allocation-regression-only"))
{
    HotPathTests.Allocations();
    return;
}
await HotPathTests.FrameReaderAsync();
await HotPathTests.ProcessFailuresAsync().WaitAsync(TimeSpan.FromSeconds(30));
if (args.Contains("--performance-only"))
{
    HotPathTests.Allocations();
    return;
}

await ProcessingStageTests.RunAsync(root);
if (args.Contains("--profiling-only"))
{
    return;
}
await AdaptiveMotionTests.RunAsync(root);
if (args.Contains("--detection-only"))
{
    DetectionObservationTests.Run();
    await AiDetectionTests.RunAsync(root);
    return;
}
await AiDetectionTests.RunAsync(root);
await AiModelRegistryTests.RunAsync(root);
DetectionObservationTests.Run();
await RecordingRetentionTests.RunAsync();
await RecordingMirrorTests.RunAsync();
Check(RecordingStorage.FreeBytes(root) > 0, "Storage capacity resolves the containing drive or mount");
var settings = new RecordingSettings { Directory = Path.Combine(root, "events-library"), TimeZoneId = "Australia/Brisbane" };
Check(settings.Validate() is null && (settings with { BufferMegabytesPerCamera = 1000 }).Validate() is not null, "Settings and RAM bounds are validated");
var rectangle = new MotionZone(.1, .1, .5, .5);
var polygon = new MotionZone(0, 0, 0, 0, Points: [new(.1, .1), new(.9, .1), new(.1, .9)]);
Check(rectangle.Contains(.2, .2) && !rectangle.Contains(.8, .8) && polygon.Contains(.2, .2) && !polygon.Contains(.8, .8), "Rectangles and freeform polygons use normalized frame coordinates");
Check(!new MotionZone(0, 0, 0, 0, Points: [new(.1, .1), new(.2, .2)]).IsValid, "Invalid polygons are rejected");
var friday = new ZoneTimeRange("22:00", "06:00", [5]);
Check(friday.Contains(new(2026, 10, 2, 23, 0, 0)) && friday.Contains(new(2026, 10, 3, 5, 59, 0)) && !friday.Contains(new(2026, 10, 3, 6, 0, 0)) && !friday.Contains(new(2026, 10, 2, 5, 0, 0)), "Overnight weekly range belongs to starting weekday and excludes its endpoint");
Check(new ZoneTimeRange("", "", [1, 2, 3, 4, 5], true).Contains(new(2026, 10, 2, 12, 0, 0)) && !new ZoneTimeRange("", "", [1, 2, 3, 4, 5], true).Contains(new(2026, 10, 3, 12, 0, 0)), "All-day rules support selected weekdays");
Check(!new ZoneTimeRange("06:00", "06:00").IsValid && !new ZoneTimeRange("01:00", "02:00", []).IsValid, "Empty weekday sets and ambiguous equal endpoints are rejected");
var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
var camera = new CameraRecordingSettings { Channel = 1, Sensitivity = 80, MinimumAreaPercent = .5 };
var detector = new AdaptiveBackgroundDetector(camera, TimeZoneInfo.Utc);
var pixels = Enumerable.Repeat((byte)50, 320 * 180).ToArray();
Check(Enumerable.Range(0, 20).All(i => !detector.Analyse(pixels, 320, 180, i * .2, now).Detected), "Static scene does not trigger");
for (var y = 30; y < 60; y++)
{
    for (var x = 30; x < 60; x++)
    {
        pixels[y * 320 + x] = 210;
    }
}

Check(!detector.Analyse(pixels, 320, 180, 4, now).Detected && !detector.Analyse(pixels, 320, 180, 4.2, now).Detected && detector.Analyse(pixels, 320, 180, 4.4, now).Detected, "Motion requires three changed frames after warmup");
var excluded = new AdaptiveBackgroundDetector(camera with { Zones = [new(0, 0, 1, 1, true)] }, TimeZoneInfo.Utc);
Check(!excluded.Analyse(pixels, 320, 180, 0, now).ScheduleActive, "Exclusion zones override the monitored area");
var scheduledZone = new MotionZone(0, 0, 1, 1, Times: [new("08:00", "09:00", [5])], Name: "Driveway");
var scheduled = new AdaptiveBackgroundDetector(camera with { Zones = [scheduledZone] }, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane"));
Check(!scheduled.Analyse(pixels, 320, 180, 0, now).ScheduleActive, "Inactive inclusion schedules do not fall back to the full picture");
Check(scheduled.Analyse(pixels, 320, 180, 10, new DateTimeOffset(2026, 10, 1, 22, 30, 0, TimeSpan.Zero)).ScheduleActive, "Schedules use the explicitly configured timezone");
var buffer = new VideoBuffer(10, 100);
for (var i = 0; i < 20; i++)
{
    buffer.Add(new(new byte[20], 1));
}

Check(buffer.Bytes <= 100 && buffer.Seconds == 5 && buffer.Limited, "Compressed RAM ring evicts old fragments at its byte limit");

var ffmpeg = new FfmpegProcess("ffmpeg");
var source = Path.Combine(root, "source.mp4");
await ffmpeg.FileAsync(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=10", "-t", "18", "-c:v", "libx264", "-preset", "ultrafast", "-g", "10", "-bf", "0", source], CancellationToken.None);
byte[]? initialization = null;
var fragments = new List<VideoFragment>();
await ffmpeg.RunAsync(["-i", source, "-c:v", "copy", "-movflags", "+empty_moov+frag_keyframe+default_base_moof", "-f", "mp4", "pipe:1"], null,
    (stream, token) => FragmentedMp4.ReadAsync(stream, bytes => { initialization = bytes; return Task.CompletedTask; }, part => { fragments.Add(part); return Task.CompletedTask; }, token), CancellationToken.None);
Check(initialization is not null && fragments.Count >= 15 && Math.Abs(fragments.Sum(f => f.DurationSeconds) - 18) < .2, "Actual FFmpeg keyframe fragments parse with correct durations");
Check(fragments.Zip(fragments.Skip(1)).All(pair => Math.Abs(pair.Second.StartSeconds - pair.First.StartSeconds - pair.First.DurationSeconds) < .001),
    "Fragment decode timestamps preserve the video clock across consecutive keyframes");
var malformedTimestamp = fragments[0].Bytes.ToArray();
var timestampType = malformedTimestamp.AsSpan().IndexOf("tfdt"u8);
malformedTimestamp[timestampType + 4] = 2;
try
{
    await FragmentedMp4.ReadAsync(new MemoryStream(initialization!.Concat(malformedTimestamp).ToArray()), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
    throw new Exception("Unsupported decode timestamp version was accepted.");
}
catch (InvalidDataException)
{
    Check(true, "Unsupported MP4 decode timestamp versions are rejected before playback");
}
Check(!Directory.Exists(settings.Directory), "Receiving and buffering compressed video creates no recording directory or files");
var store = new FileRecordingStore(settings, ffmpeg);
var selected = fragments.TakeLast(10).ToArray();
await using (var writer = await store.BeginAsync(1, "Test camera", initialization!, 10, CancellationToken.None))
{
    foreach (var part in selected)
    {
        await writer.AppendAsync(part, CancellationToken.None);
    }

    var additionalMotion = new RecordingTrigger(Zones: [new(Guid.NewGuid().ToString("N"), "Side gate")]);
    await writer.AddTriggerAsync(additionalMotion, CancellationToken.None);
    await writer.AddTriggerAsync(additionalMotion, CancellationToken.None);
    await writer.AddTriggerAsync(new("sensor", "Contact opened", "unit-request"), CancellationToken.None);
    await writer.CompleteAsync(false, CancellationToken.None);
}
await store.MaintainAsync(CancellationToken.None);
var recorded = store.List(null, null).Single();
Check(recorded.Status == "ready" && recorded.DurationSeconds >= 9.9, "A triggered event saves its pre-event buffer and finalizes to MP4");
Check(recorded.AdditionalTriggers.Length == 2 && store.FindByRequest("unit-request").Single().Id == recorded.Id,
    "Ongoing clips retain new motion zones once and correlate additional external triggers");
Check(store.MediaPath(recorded.Id, true) is not null, "Finalization produces a thumbnail near the motion trigger");
var playable = store.MediaPath(recorded.Id)!;
await ffmpeg.FileAsync(["-xerror", "-i", playable, "-f", "null", "-"], CancellationToken.None);
Check(true, "A clip cut from a later keyframe decodes completely");
var probeInfo = new ProcessStartInfo("ffprobe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var argument in new[] { "-v", "error", "-show_entries", "format=start_time,duration:stream=codec_name", "-of", "json", playable })
{
    probeInfo.ArgumentList.Add(argument);
}

using (var probe = Process.Start(probeInfo)!)
{
    var json = JsonDocument.Parse(await probe.StandardOutput.ReadToEndAsync());
    await probe.WaitForExitAsync();
    Check(probe.ExitCode == 0 && double.Parse(json.RootElement.GetProperty("format").GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture) < .2, "Finalized clips reset their playback timeline to zero");
    Check(json.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString() == "h264", "Recording preserves the source codec without re-encoding");
}
await store.ProtectAsync(recorded.Id, true, CancellationToken.None);
Check(!await store.DeleteAsync(recorded.Id, CancellationToken.None) && store.MediaPath("../" + recorded.Id) is null, "Protection and media path validation are enforced");
var recovered = new FileRecordingStore(settings, ffmpeg);
Check(recovered.List(null, null).Single().Protected, "The library and protection state survive a restart");
var managedDirectory = Path.Combine(settings.Directory, "events");
Directory.CreateDirectory(managedDirectory);
var interrupted = new RecordingEvent { Channel = 1, CameraName = "Interrupted camera", StartedAt = now, TriggeredAt = now, Status = "recording" };
await File.WriteAllTextAsync(Path.Combine(managedDirectory, interrupted.Id + ".json"), JsonSerializer.Serialize(interrupted));
await using (var capture = File.Create(Path.Combine(managedDirectory, interrupted.Id + ".capture.mp4")))
{
    await capture.WriteAsync(initialization!);
    foreach (var part in selected.Take(3))
    {
        await capture.WriteAsync(part.Bytes);
    }

    await capture.WriteAsync(new byte[] { 0, 0, 0 }); // Power loss in the next box header.
}
var restart = new FileRecordingStore(settings with { RetentionDays = 365 }, ffmpeg);
await restart.MaintainAsync(CancellationToken.None);
var recoveredClip = restart.List(null, null).Single(e => e.Id == interrupted.Id);
Check(recoveredClip.Status == "ready" && recoveredClip.Interrupted && recoveredClip.DurationSeconds >= 2.9, "Startup recovers complete keyframe fragments and duration after an interrupted write");
var unrelated = Path.Combine(managedDirectory, "keep-this.txt");
await File.WriteAllTextAsync(unrelated, "unmanaged");
var expired = recorded with { Id = Guid.NewGuid().ToString("N"), TriggeredAt = DateTimeOffset.UtcNow.AddDays(-30), Protected = false };
await File.WriteAllTextAsync(Path.Combine(managedDirectory, expired.Id + ".json"), JsonSerializer.Serialize(expired));
File.Copy(playable, Path.Combine(managedDirectory, expired.Id + ".mp4"));
var retention = new FileRecordingStore(settings, ffmpeg);
await retention.MaintainAsync(CancellationToken.None);
Check(!retention.List(null, null).Any(e => e.Id == expired.Id) && File.Exists(unrelated) && retention.List(null, null).Any(e => e.Id == recorded.Id && e.Protected), "Retention deletes only expired owned events and preserves protected and unrelated files");
try
{
    var full = new FileRecordingStore(settings with
    {
        MaximumStorageGigabytes = .000001
    }, ffmpeg);
    await full.BeginAsync(1, "Quota", initialization!, 0, CancellationToken.None);
    throw new Exception("Missing quota enforcement");
}
catch (IOException)
{
    Check(true, "Protected footage cannot be evicted to admit an event beyond the storage budget");
}
try
{
    await FragmentedMp4.ReadAsync(new MemoryStream([0x7f, 0xff, 0xff, 0xff, 109, 100, 97, 116]), _ => Task.CompletedTask, _ => Task.CompletedTask, CancellationToken.None);
    throw new Exception("Missing box bound");
}
catch (InvalidDataException)
{
    Check(true, "Malformed oversized MP4 boxes are rejected before allocating their payload");
}
await ReplacementDetectorTests.RunAsync(root);
await ContinuousRecordingTests.RunAsync(root);
await RecordingArchiveTests.RunAsync(root, ffmpeg, initialization!, fragments.ToArray());
await MotionActionTests.RunAsync(root);
await AudioRecordingTests.RunAsync(root);
if (args.Contains("--unit-only"))
{
    Console.WriteLine("All recording component checks passed.");
    return;
}

var rtsp = Port();
var web = Port();
var connectionFile = Path.Combine(root, "connection.json");
var stillFile = Path.Combine(root, "still");
var connection = new ViewerSettings { Host = "127.0.0.1", RtspPort = rtsp, Username = "viewer", ChannelCount = 4, AutoConnect = false };
var integrationSettings = settings with
{
    Directory = Path.Combine(root, "integration-library"),
    Enabled = true,
    PreCaptureSeconds = 3,
    PostCaptureSeconds = 2,
    Cameras = [.. Enumerable.Range(1, 4).Select(channel => camera with { Channel = channel, DetectionSubstream = false })]
};
await File.WriteAllTextAsync(connectionFile, JsonSerializer.Serialize(new AppSettings { Configurations = [new() { Connection = connection, Password = "local-only-test", Recording = integrationSettings }] }));
await File.WriteAllTextAsync(stillFile, "");
Process? fixture = null, server = null;
try
{
    fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
    {
        ["RTSP_FIXTURE_PORT"] = rtsp.ToString(),
        ["RTSP_FIXTURE_STILL_FILE"] = stillFile,
        ["RTSP_FIXTURE_AUDIO_CHANNELS"] = "1,3"
    });
    await Until(async () => (await RtspProbe.DescribeAsync(connection, "local-only-test")).Success, "Recording fixture starts");
    var serverAssembly = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/recording-web/SecurityCameraViewer.Web.dll");
    server = Start("dotnet", [serverAssembly, "--urls", $"http://127.0.0.1:{web}", "--settings", connectionFile,
        "--ai-model-directory", Path.Combine(root, "missing-models")]);
    using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{web}"), Timeout = TimeSpan.FromSeconds(40) };
    await Until(async () => { try { return (await http.GetAsync("/api/recording")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "Recording web host starts");
    async Task<JsonElement> State() => await http.GetFromJsonAsync<JsonElement>("/api/recording");
    async Task<RecordingEvent[]> Events() => (await http.GetFromJsonAsync<RecordingEvent[]>("/api/recordings"))!;
    var catalog = await http.GetFromJsonAsync<JsonElement>("/api/recording/detection");
    Check(catalog.GetProperty("methods").GetArrayLength() == 3 && catalog.GetProperty("models").GetArrayLength() == 3,
        "Detection catalog exposes selectable methods and local models");
    await Until(async () => (await State()).GetProperty("cameras").EnumerateArray().Count(c => c.GetProperty("bufferSeconds").GetDouble() >= 5) == 4, "Four cameras arm and fill RAM with live viewing disconnected");
    Check(!Directory.Exists(integrationSettings.Directory) && (await Events()).Length == 0, "Idle motion monitoring makes zero video or metadata writes");
    File.Delete(stillFile);
    await Until(async () => (await Events()).Where(e => e.Status == "recording").Select(e => e.Channel).Distinct().Count() == 4, "Real software motion detection starts events on four cameras");
    await Until(async () =>
    {
        var observations = await http.GetFromJsonAsync<JsonElement>("/api/detections");
        return observations.GetProperty("cameras").EnumerateArray().Any(camera => camera.GetProperty("detections").EnumerateArray()
            .Any(item => item.GetProperty("observation").GetProperty("confirmed").GetBoolean() && item.GetProperty("remainingSeconds").GetDouble() > 0));
    }, "Live detection API publishes fresh confirmed motion regions from the real RTSP pipeline");
    var firstId = (await Events())[0].Id;
    (await http.PostAsync("/api/quality", null)).EnsureSuccessStatusCode();
    (await http.PostAsync("/api/disconnect", null)).EnsureSuccessStatusCode();
    Check((await Events()).Any(e => e.Id == firstId && e.Status == "recording"), "Viewing quality and disconnect do not stop the event recorder");
    await File.WriteAllTextAsync(stillFile, "");
    await Until(async () => (await Events()).Any(e => e.Status == "ready"), "Quiet-time tail completes and finalizes the event", 50);
    var clip = (await Events()).First(e => e.Status == "ready");
    Check(clip.DurationSeconds >= 5, "Event includes pre-capture footage and its quiet-time tail");
    await Until(async () => (await Events()).Where(e => e.Status == "ready").Select(e => e.Channel).Distinct().Count() == 4,
        "Mixed cameras with and without RTSP audio all finalize", 50);
    foreach (var completed in (await Events()).Where(e => e.Status == "ready").DistinctBy(e => e.Channel))
    {
        var clipPath = Directory.EnumerateFiles(integrationSettings.Directory, "*" + completed.Id + ".mp4", SearchOption.AllDirectories).Single();
        await AudioRecordingTests.VerifyClipAsync(clipPath, ffmpeg, completed.DurationSeconds,
            expectAudio: completed.Channel is 1 or 3);
    }
    using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/recordings/{clip.Id}/video");
    range.Headers.Range = new(0, 99);
    Check((await http.SendAsync(range)).StatusCode == HttpStatusCode.PartialContent, "Browser playback supports byte-range seeking");
    using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/recording") { Content = JsonContent.Create(integrationSettings) };
    foreign.Headers.Add("Origin", "http://other.example");
    Check((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "Recording mutations retain same-origin checks");
    File.Delete(stillFile);
    await Until(async () => (await Events()).Any(e => e.Status == "recording"), "A later motion event starts");
    await Stop(fixture);
    fixture = null;
    await Until(async () => (await State()).GetProperty("cameras").EnumerateArray().Any(c => c.GetProperty("status").GetString() == "retrying"), "Camera loss is reported independently of live viewing");
    fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
    {
        ["RTSP_FIXTURE_PORT"] = rtsp.ToString(),
        ["RTSP_FIXTURE_STILL_FILE"] = stillFile,
        ["RTSP_FIXTURE_AUDIO_CHANNELS"] = "1,3"
    });
    await Until(async () => (await State()).GetProperty("cameras").EnumerateArray().Count(c => c.GetProperty("bufferSeconds").GetDouble() >= 2) == 4, "All four recording workers recover after the recorder restarts", 50);
    Check((await Events()).Any(e => e.Interrupted), "Connection loss marks interrupted captures instead of joining incompatible stream epochs");
    var invalid = integrationSettings with
    {
        Cameras = [camera with { Zones = [new(0, 0, 1, 1, Times: [new("23:00", "06:00", [])])] }]
    };
    Check((await http.PostAsJsonAsync("/api/recording", invalid)).StatusCode == HttpStatusCode.BadRequest, "API rejects invalid weekly zone schedules");
    Check((await http.PostAsJsonAsync("/api/recording", integrationSettings with
    {
        Cameras = [camera with { DetectionMethod = "unknown" }]
    })).StatusCode == HttpStatusCode.BadRequest, "API rejects unknown detection methods");
    var offSchedule = integrationSettings with
    {
        Cameras = [camera with { Zones = [new(0, 0, 1, 1, Times: [new("00:00", "00:01", [((int)DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(10)).DayOfWeek + 3) % 7])])] }]
    };
    (await http.PostAsJsonAsync("/api/recording", offSchedule)).EnsureSuccessStatusCode();
    File.Delete(stillFile);
    await Until(async () => (await State()).GetProperty("cameras")[0].GetProperty("status").GetString() == "scheduled", "Inactive weekly inclusion zones pause detection even on moving video");
    (await http.PostAsJsonAsync("/api/recording", integrationSettings with
    {
        Cameras = [camera with { DetectionMethod = DetectionMethods.Ai, AnalysisWidth = 640 }]
    })).EnsureSuccessStatusCode();
    await Until(async () =>
    {
        var state = (await State()).GetProperty("cameras")[0];
        return state.GetProperty("status").GetString() == "analysis-error" &&
            state.GetProperty("ai").GetProperty("message").GetString()!.Contains("not installed") &&
            state.GetProperty("bufferSeconds").GetDouble() >= 3;
    }, "RGB AI analysis reports a missing model while encoded RAM buffering continues");
    Check(!(await Events()).Any(item => item.Status == "recording"),
        "Missing AI models cannot turn moving pixels into unconfirmed recording events");
    var continuousSettings = integrationSettings with
    {
        Cameras = [camera with { Mode = RecordingModes.Continuous }]
    };
    await File.WriteAllTextAsync(stillFile, "");
    (await http.PostAsJsonAsync("/api/recording", continuousSettings)).EnsureSuccessStatusCode();
    await Until(async () => (await Events()).Any(item => item.Mode == RecordingModes.Continuous && item.Status == "recording"),
        "Continuous mode starts on a still RTSP scene without a motion trigger");
    var continuousState = (await State()).GetProperty("cameras")[0];
    Check(continuousState.GetProperty("bufferBytes").GetInt64() == 0 && !continuousState.GetProperty("motion").GetBoolean(),
        "RTSP continuous recording avoids detection and pre-event RAM buffering by default");
    (await http.PostAsJsonAsync("/api/recording", continuousSettings with
    {
        Cameras = [camera with { Mode = RecordingModes.Continuous, DetectDuringContinuousRecording = true }]
    })).EnsureSuccessStatusCode();
    File.Delete(stillFile);
    await Until(async () =>
    {
        var recording = (await Events()).FirstOrDefault(item => item.Mode == RecordingModes.Continuous && item.Status == "recording");
        if (recording is null)
        {
            return false;
        }
        var markers = await http.GetFromJsonAsync<RecordingTrigger[]>($"/api/recordings/{recording.Id}/markers");
        return markers!.Any(marker => marker.Source == "motion");
    }, "Optional RTSP detection adds motion markers to continuous footage");
    (await http.PostAsJsonAsync("/api/recording", integrationSettings with
    {
        Enabled = false
    })).EnsureSuccessStatusCode();
    Check((await State()).GetProperty("cameras").GetArrayLength() == 0, "Disabling recording stops every capture and detector worker");
    var continuousClip = (await Events()).First(item => item.Mode == RecordingModes.Continuous && item.Status == "ready");
    var coverage = await http.GetFromJsonAsync<RecordingTimeline>(
        $"/api/recordings/timeline?channel=1&since={Uri.EscapeDataString(continuousClip.StartedAt.AddMinutes(-1).ToString("O"))}&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"))}");
    Check(coverage!.Recordings.Any(item => item.Id == continuousClip.Id) && !coverage.Truncated,
        "The coverage API exposes finalized continuous footage by camera and actual recording time");
    Check((await http.GetAsync("/api/recordings/timeline?channel=1&since=2026-01-01T00:00:00Z&before=2026-01-05T00:00:00Z")).StatusCode == HttpStatusCode.BadRequest,
        "Timeline requests reject unbounded time ranges");
    var continuousPath = Directory.EnumerateFiles(integrationSettings.Directory, "*" + continuousClip.Id + ".mp4", SearchOption.AllDirectories).Single();
    await AudioRecordingTests.VerifyClipAsync(continuousPath, ffmpeg, continuousClip.DurationSeconds, expectAudio: true);

    var mirrorDirectory = Path.Combine(root, "mirror");
    Directory.CreateDirectory(mirrorDirectory);
    (await http.PostAsJsonAsync("/api/recording", continuousSettings with
    {
        Mirrors = [new(mirrorDirectory, 1)]
    })).EnsureSuccessStatusCode();
    await Until(async () => (await State()).GetProperty("storageLocations").EnumerateArray().Any(location =>
        location.GetProperty("directory").GetString() == mirrorDirectory && location.GetProperty("canActivate").GetBoolean()),
        "The web settings API registers a mirror and backfills completed RTSP clips");
    var primaryMarker = Path.Combine(integrationSettings.Directory, ".recording-location-id");
    var savedMarker = Path.Combine(root, "detached-location-id");
    File.Move(primaryMarker, savedMarker);
    await Until(async () => (await State()).GetProperty("storageState").GetProperty("activeDirectory").GetString() == mirrorDirectory &&
        (await Events()).Any(item => item.Mode == RecordingModes.Continuous && item.Status == "recording"),
        "The RTSP worker resumes continuous capture on a mirror after the primary becomes unavailable");
    using var mirroredVideo = new HttpRequestMessage(HttpMethod.Get, $"/api/recordings/{continuousClip.Id}/video");
    mirroredVideo.Headers.Range = new(0, 99);
    Check((await http.SendAsync(mirroredVideo)).StatusCode == HttpStatusCode.PartialContent,
        "The promoted mirror serves copied clips through the existing playback URLs");
    File.Move(savedMarker, primaryMarker);
    await Until(async () => (await State()).GetProperty("storageLocations").EnumerateArray().Any(location =>
        location.GetProperty("directory").GetString() == integrationSettings.Directory && location.GetProperty("canActivate").GetBoolean()),
        "The original location reappears as a secondary");
    (await http.PostAsJsonAsync("/api/recording/storage/activate", new
    {
        integrationSettings.Directory, integrationSettings.ConfigurationId
    })).EnsureSuccessStatusCode();
    Check((await State()).GetProperty("storageState").GetProperty("activeDirectory").GetString() == integrationSettings.Directory,
        "The web settings API can switch back to the original location");

    Check((await File.ReadAllTextAsync(connectionFile)).Contains("local-only-test"), "Recording settings updates preserve saved camera credentials");
    Console.WriteLine("All motion recording checks passed.");
}
finally
{
    await Stop(server);
    await Stop(fixture);
    File.Delete(stillFile);
}
