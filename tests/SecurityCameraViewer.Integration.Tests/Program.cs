using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

static void Check(bool value, string name) { if (!value) { throw new Exception("FAIL: " + name); } Console.WriteLine("PASS: " + name); }
static async Task Until(Func<Task<bool>> condition, string name, int seconds = 40)
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
        foreach (var pair in environment)
        {
            info.Environment[pair.Key] = pair.Value;
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

var root = Path.GetFullPath(Path.Combine("artifacts", "integration-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
await MotionActionWebhookTests.RunAsync(root);
if (args.Contains("--actions-only"))
{
    return;
}

if (args.Contains("--queue-lifecycle-only"))
{
    var emptyConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["settings"] = Path.Combine(root, "settings.json") }).Build();
    var emptySettings = new IntegrationSettings(new JsonAppSettingsStore(emptyConfig), emptyConfig);
    var boundedHub = new IntegrationEventHub();
    for (var i = 0; i < 1025; i++)
    {
        boundedHub.Publish(new("motion.started", 1));
    }

    Check(boundedHub.Dropped == 1, "Event admission is bounded and counts overflow");
    using var queue = new WebhookDispatcher(emptySettings, boundedHub);
    await queue.StartAsync(CancellationToken.None);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await queue.StopAsync(deadline.Token);
    Check(boundedHub.Pending.Reader.Count == 0, "Graceful shutdown drains admitted events before cancelling delivery");
    Check(!Directory.Exists(emptySettings.DirectoryPath), "Idle dispatcher creates no files or access configuration");
    return;
}
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["settings"] = Path.Combine(root, "unit", "settings.json") }).Build();
var settings = new IntegrationSettings(new JsonAppSettingsStore(configuration), configuration);
var (Token, Value) = settings.CreateToken("Test", ["read"], null);
Check(settings.Authenticate(Value)?.Scopes.SequenceEqual(["read"]) == true && settings.Authenticate("wrong") is null, "Tokens authenticate with explicit scopes");
Check(!File.ReadAllText(configuration["settings"]!).Contains(Value), "Plaintext API tokens are never persisted");
var left = new MotionZone(0, 0, .5, 1, Name: "Left") { Id = Guid.NewGuid().ToString("N") };
var right = new MotionZone(.5, 0, .5, 1, Name: "Right") { Id = Guid.NewGuid().ToString("N") };
var detector = new AdaptiveBackgroundDetector(new() { Zones = [left, right], MinimumAreaPercent = .5, Sensitivity = 80 }, TimeZoneInfo.Utc);
var pixels = Enumerable.Repeat((byte)50, 100 * 60).ToArray();
for (var i = 0; i < 20; i++)
{
    detector.Analyse(pixels, 100, 60, i * .2, DateTimeOffset.UtcNow);
}

for (var y = 10; y < 20; y++)
{
    for (var x = 10; x < 20; x++)
    {
        pixels[y * 100 + x] = 220;
    }
}

MotionSample sample = default;
for (var i = 20; i < 24; i++)
{
    sample = detector.Analyse(pixels, 100, 60, i * .2, DateTimeOffset.UtcNow);
}

Check(sample.Detected && sample.Zones!.Single(z => z.Id == left.Id).Detected && !sample.Zones!.Single(z => z.Id == right.Id).Detected, "Per-zone motion reports only the moving named area");
var targetPort = Port();
var hook = settings.SaveWebhook(new() { Name = "Receiver", Url = $"http://127.0.0.1:{targetPort}/events", Events = ["motion.started"], CameraIds = ["camera-1"], ZoneIds = [left.Id] }, false);
var motion = new CameraEvent("motion.started", 1, "Test", [new(left.Id, left.Name)]);
Check(WebhookDispatcher.Matches(hook, motion) && !WebhookDispatcher.Matches(hook, motion with { Channel = 2 }) && !WebhookDispatcher.Matches(hook, motion with { Zones = [new(right.Id, right.Name)] }), "Webhook event, camera and zone filters intersect");
var hub = new IntegrationEventHub();
using (var dispatcher = new WebhookDispatcher(settings, hub))
{
    Check(await dispatcher.Enqueue(hook, motion, CancellationToken.None) is not null && await dispatcher.Enqueue(hook, motion, CancellationToken.None) is null &&
        await dispatcher.Enqueue(hook, motion with { Id = Guid.NewGuid().ToString("N") }, CancellationToken.None) is null, "Persistent queue deduplicates events and applies cooldown");
}
using var receiver = new TcpListener(IPAddress.Loopback, targetPort);
receiver.Start();
var deliveredIds = new List<string>();
var receiverTask = Task.Run(async () =>
{
    for (var attempt = 0; attempt < 2; attempt++)
    {
        using var client = await receiver.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _ = await reader.ReadLineAsync();
        while (await reader.ReadLineAsync() is { Length: > 0 } line)
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        var body = new char[int.Parse(headers["Content-Length"])];
        if (await reader.ReadBlockAsync(body) != body.Length)
        {
            throw new IOException("Truncated request");
        }

        var payload = new string(body);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hook.Secret), Encoding.UTF8.GetBytes(headers["X-SCV-Timestamp"] + "." + payload)));
        Check(headers["X-SCV-Signature"] == expected, "Delivery HMAC signs exact timestamp and JSON bytes");
        deliveredIds.Add(headers["X-SCV-Delivery"]);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(attempt == 0 ? "503 Unavailable" : "204 No Content")}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    }
});
using (var dispatcher = new WebhookDispatcher(settings, new IntegrationEventHub()))
{
    Check(dispatcher.PendingCount == 1, "Pending delivery survives dispatcher restart");
    await dispatcher.StartAsync(CancellationToken.None);
    await Until(() => Task.FromResult(dispatcher.List().Single().Status == "delivered"), "503 is retried and then acknowledged");
    await receiverTask.WaitAsync(TimeSpan.FromSeconds(10));
    Check(deliveredIds.Distinct().Count() == 1 && dispatcher.List().Single().Attempts == 2, "Retries retain delivery IDs and attempt history");
    await dispatcher.StopAsync(CancellationToken.None);
}

var rtsp = Port();
var web = Port();
var connectionFile = Path.Combine(root, "connection.json");
var modelDirectory = Path.Combine(root, "models");
Directory.CreateDirectory(modelDirectory);
var still = Path.Combine(root, "still");
var connection = new ViewerSettings { Host = "127.0.0.1", RtspPort = rtsp, Username = "viewer", ChannelCount = 1, AutoConnect = false };
var recording = new RecordingSettings
{
    Enabled = true,
    Directory = Path.Combine(root, "recordings"),
    PreCaptureSeconds = 3,
    PostCaptureSeconds = 2,
    Cameras = [new() { Channel = 1, DetectionSubstream = false, Zones = [left, right] }]
};
File.WriteAllText(connectionFile, JsonSerializer.Serialize(new AppSettings { Configurations = [new() { Connection = connection, Password = "local-only-test", Recording = recording }] }));
File.WriteAllText(still, "");
Process? fixture = null, server = null;
try
{
    fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
    {
        ["RTSP_FIXTURE_PORT"] = rtsp.ToString(),
        ["RTSP_FIXTURE_STILL_FILE"] = still
    });
    await Until(async () => (await RtspProbe.DescribeAsync(connection, "local-only-test")).Success, "Integration camera fixture starts");
    var assembly = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/web/SecurityCameraViewer.Web.dll");
    string[] serverArgs = [assembly, "--urls", $"http://127.0.0.1:{web}", "--settings", connectionFile, "--ai-model-directory", modelDirectory];
    server = Start("dotnet", serverArgs);
    using var anonymous = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new($"http://127.0.0.1:{web}") };
    await Until(async () => { try { return (await anonymous.GetAsync("/api/auth/status")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "Integration web host starts");
    Check((await anonymous.GetAsync("/api/v1/cameras")).StatusCode == HttpStatusCode.Unauthorized, "Versioned API requires tokens even before setup");
    var setup = await anonymous.PostAsync("/api/integrations/setup", null);
    setup.EnsureSuccessStatusCode();
    var adminToken = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    using var admin = new HttpClient { BaseAddress = anonymous.BaseAddress };
    admin.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);
    Check((await anonymous.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized && (await anonymous.PostAsync("/api/connect", null)).StatusCode == HttpStatusCode.Unauthorized, "Setup protects legacy read and mutation routes");
    async Task<(HttpClient Client, string Id)> Client(string[] scopes)
    {
        var response = await admin.PostAsJsonAsync("/api/integrations/tokens", new
        {
            name = string.Join(',', scopes),
            scopes,
            expiresAt = (string?)null
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = anonymous.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new("Bearer", result.GetProperty("token").GetString());
        return (client, result.GetProperty("details").GetProperty("id").GetString()!);
    }
    var readerInfo = await Client(["read"]);
    using var read = readerInfo.Client;
    var triggerInfo = await Client(["trigger"]);
    using var triggerClient = triggerInfo.Client;
    Check((await read.GetAsync("/api/v1/cameras")).IsSuccessStatusCode && (await read.GetAsync("/api/integrations")).StatusCode == HttpStatusCode.Forbidden &&
        (await read.PostAsync("/api/connect", null)).StatusCode == HttpStatusCode.Forbidden && (await triggerClient.GetAsync("/api/v1/recordings")).StatusCode == HttpStatusCode.Forbidden, "Scopes protect API administration, legacy controls and recording access");
    Check((await read.GetAsync("/api/v1/openapi.json")).IsSuccessStatusCode, "Published OpenAPI specification is available");
    var timelineQuery = $"cameraId=camera-1&since={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}";
    Check((await read.GetAsync("/api/v1/recordings/timeline?" + timelineQuery)).IsSuccessStatusCode &&
        (await triggerClient.GetAsync("/api/v1/recordings/timeline?" + timelineQuery)).StatusCode == HttpStatusCode.Forbidden &&
        (await triggerClient.GetAsync("/api/v1/recordings/00000000000000000000000000000000/markers")).StatusCode == HttpStatusCode.Forbidden,
        "Archive coverage and markers require read scope");
    Check((await read.GetAsync("/api/recording/detection")).StatusCode == HttpStatusCode.Forbidden &&
        (await admin.GetAsync("/api/recording/detection")).IsSuccessStatusCode,
        "Local model paths and GPU discovery require administrator scope");
    Check((await read.PostAsync("/api/recording/detection/reload", null)).StatusCode == HttpStatusCode.Forbidden &&
        (await triggerClient.PostAsync("/api/recording/detection/reload", null)).StatusCode == HttpStatusCode.Forbidden,
        "Model discovery refresh requires administrator scope");
    await File.WriteAllTextAsync(Path.Combine(modelDirectory, "parcel.model.json"), JsonSerializer.Serialize(new AiModelDefinition(
        "parcel-model", "Parcel camera", "parcel.onnx", 32, "ultralytics-raw", "Test")
    {
        Labels = ["parcel", "cat", "person"]
    }));
    var refreshed = await admin.PostAsync("/api/recording/detection/reload", null);
    refreshed.EnsureSuccessStatusCode();
    var catalogue = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
    var parcel = catalogue.GetProperty("models").EnumerateArray().Single(model => model.GetProperty("id").GetString() == "parcel-model");
    Check(parcel.GetProperty("classes")[0].GetString() == "parcel" && parcel.GetProperty("options").GetArrayLength() == 4 &&
        parcel.GetProperty("presets").GetArrayLength() == 1 && !parcel.GetProperty("installed").GetBoolean(),
        "Refresh discovers custom classes, options and presets without a server restart or model session");
    var customRecording = recording with
    {
        Cameras = [recording.Cameras[0] with
    {
        AiModel = "parcel-model", AiClasses = ["parcel", "person"], AiIgnoreClasses = ["cat"]
    }]
    };
    Check((await admin.PutAsJsonAsync("/api/v1/settings/recording", customRecording with
    {
        Cameras = [customRecording.Cameras[0] with { AiIgnoreClasses = ["person"] }]
    })).StatusCode == HttpStatusCode.BadRequest && (await admin.PostAsJsonAsync("/api/recording", customRecording with
    {
        Cameras = [customRecording.Cameras[0] with { AiClasses = ["unsupported"] }]
    })).StatusCode == HttpStatusCode.BadRequest,
        "Versioned and browser settings reject overlapping roles and unsupported model labels");
    var detections = await read.GetFromJsonAsync<JsonElement>("/api/v1/detections");
    Check(detections.GetProperty("configurationId").GetString() == connection.ConfigurationId &&
        !detections.TryGetProperty("modelDirectory", out _) &&
        (await triggerClient.GetAsync("/api/detections")).StatusCode == HttpStatusCode.Forbidden,
        "Detection overlays use read scope without exposing model paths or broadening trigger tokens");
    await Until(async () =>
    {
        detections = await read.GetFromJsonAsync<JsonElement>("/api/v1/detections");
        return detections.GetProperty("cameras")[0].GetProperty("history").GetProperty("frames").GetArrayLength() >= 2;
    }, "Detection history includes sampled frames from a still camera");
    var history = detections.GetProperty("cameras")[0].GetProperty("history");
    Check(detections.GetProperty("historySeconds").GetDouble() > 0 &&
        detections.GetProperty("maximumHistorySamples").GetInt32() > 0 &&
        history.GetProperty("frames").EnumerateArray().Any(frame => frame.GetProperty("detections").GetArrayLength() == 0),
        "Detection API exposes metadata budgets and empty samples for buffered playback");
    var publicationCursor = detections.GetProperty("generatedAt").GetDateTimeOffset();
    var cursorQuery = "?since=" + Uri.EscapeDataString(publicationCursor.ToString("O"));
    var incremental = await read.GetFromJsonAsync<JsonElement>("/api/v1/detections" + cursorQuery);
    var browserHistory = await read.GetFromJsonAsync<JsonElement>("/api/detections" + cursorQuery);
    Check(incremental.GetProperty("cameras")[0].GetProperty("history").GetProperty("id").GetString() == history.GetProperty("id").GetString() &&
        incremental.GetProperty("cameras")[0].GetProperty("history").GetProperty("frames").EnumerateArray()
            .All(frame => frame.GetProperty("publishedAt").GetDateTimeOffset() >= publicationCursor) &&
        browserHistory.GetProperty("cameras")[0].GetProperty("history").GetProperty("frames").EnumerateArray()
            .All(frame => frame.GetProperty("publishedAt").GetDateTimeOffset() >= publicationCursor),
        "Versioned and browser detection endpoints accept the inclusive publication cursor");
    Check((await read.GetAsync("/api/v1/detections?since=invalid")).StatusCode == HttpStatusCode.BadRequest &&
        (await read.GetAsync("/api/detections?since=invalid")).StatusCode == HttpStatusCode.BadRequest &&
        (await triggerClient.GetAsync("/api/v1/detections" + cursorQuery)).StatusCode == HttpStatusCode.Forbidden,
        "Detection cursors reject malformed input and preserve read authorization");
    Check((await read.GetAsync("/api/setup/metadata/export")).StatusCode == HttpStatusCode.Forbidden &&
        (await read.GetAsync("/api/setup/configurations")).StatusCode == HttpStatusCode.Forbidden &&
        (await admin.GetAsync("/api/setup/metadata/export")).IsSuccessStatusCode,
        "Camera addresses, ONVIF setup and diagnostic exports require administrator scope");
    Check((await read.PostAsync("/api/cameras/1/check", null)).StatusCode == HttpStatusCode.Forbidden &&
        (await triggerClient.PostAsync("/api/cameras/1/check", null)).StatusCode == HttpStatusCode.Forbidden,
        "Battery Check now requires administrator scope");
    await Until(async () => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/health")).GetProperty("cameras")[0].GetProperty("preEventSeconds").GetDouble() >= 3, "API reports filled pre-event RAM coverage");
    Check(!Directory.Exists(recording.Directory), "Idle integrations still write no video");
    using var sse = await read.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);
    using var eventReader = new StreamReader(await sse.Content.ReadAsStreamAsync());
    Check(await eventReader.ReadLineAsync() == "event: connected", "SSE connects with explicit resynchronization instructions");
    (await admin.PutAsJsonAsync("/api/v1/cameras/camera-1/arming", new
    {
        mode = "disarmed",
        expiresAt = DateTimeOffset.UtcNow.AddSeconds(4)
    })).EnsureSuccessStatusCode();
    var body = new TriggerRequest("camera-1", "home-assistant", "Door opened", 2);
    async Task<HttpResponseMessage> Trigger(HttpClient client, string key, TriggerRequest request)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/recordings/trigger") { Content = JsonContent.Create(request) };
        message.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(message);
    }
    Check((await Trigger(triggerClient, "disarmed", body)).StatusCode == HttpStatusCode.Conflict &&
        (await Trigger(triggerClient, "override", body with
        {
            OverrideSchedule = true
        })).StatusCode == HttpStatusCode.Forbidden, "Trigger-only tokens respect disarming and cannot override schedules");
    await Until(async () => (await admin.GetFromJsonAsync<JsonElement>("/api/v1/cameras/camera-1/arming")).GetProperty("effectiveMode").GetString() == "schedule", "Temporary disarming expires back to schedule");
    var accepted = await Trigger(triggerClient, "door-1", body);
    accepted.EnsureSuccessStatusCode();
    var receipt = await accepted.Content.ReadFromJsonAsync<TriggerReceipt>();
    var duplicate = await Trigger(triggerClient, "door-1", body);
    duplicate.EnsureSuccessStatusCode();
    Check((await duplicate.Content.ReadFromJsonAsync<TriggerReceipt>())!.Id == receipt!.Id, "Duplicate trigger requests return the original receipt");
    Check((await Trigger(triggerClient, "door-1", body with
    {
        Reason = "Different"
    })).StatusCode == HttpStatusCode.Conflict, "Reusing an idempotency key with different input is rejected");
    await Until(async () => (await read.GetFromJsonAsync<JsonElement>("/api/v1/recordings")).EnumerateArray().Any(e => e.GetProperty("status").GetString() == "ready"), "External trigger creates a playable recording from a still scene");
    var clips = await read.GetFromJsonAsync<JsonElement>("/api/v1/recordings");
    var clip = clips[0];
    Check(clips.GetArrayLength() == 1 && clip.GetProperty("trigger").GetProperty("source").GetString() == "home-assistant" && clip.GetProperty("durationSeconds").GetDouble() >= 5, "One clip preserves external source and includes RAM pre-roll");
    Check((await read.GetAsync(clip.GetProperty("videoUrl").GetString())).IsSuccessStatusCode, "Scoped read tokens can retrieve ready video");
    var timeline = await read.GetFromJsonAsync<JsonElement>("/api/v1/recordings/timeline?" + timelineQuery);
    Check(timeline.GetProperty("recordings")[0].GetProperty("id").GetString() == clip.GetProperty("id").GetString() &&
        timeline.GetProperty("recordings")[0].GetProperty("mode").GetString() == "event",
        "Versioned coverage uses stable camera IDs and consistent recording metadata");
    var markers = await read.GetFromJsonAsync<RecordingTrigger[]>($"/api/v1/recordings/{clip.GetProperty("id").GetString()}/markers")
        ?? throw new Exception("Missing activity markers response");
    Check(markers.Single().Source == "home-assistant" && markers[0].RequestId == receipt!.Id,
        "Scoped readers can seek to persisted external activity markers");
    (await admin.PutAsJsonAsync("/api/v1/settings/recording", customRecording)).EnsureSuccessStatusCode();
    await Stop(server);
    server = Start("dotnet", serverArgs);
    await Until(async () => { try { return (await admin.GetAsync("/api/v1/health")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "API tokens remain valid after restart");
    var restored = await admin.GetFromJsonAsync<RecordingSettings>("/api/v1/settings/recording");
    Check(restored!.Cameras[0].AiModel == "parcel-model" && restored.Cameras[0].AiClasses.SequenceEqual(["parcel", "person"]) &&
        restored.Cameras[0].AiIgnoreClasses.SequenceEqual(["cat"]), "Custom model trigger and ignore rules survive settings replacement and restart");
    Check((await (await Trigger(triggerClient, "door-1", body)).Content.ReadFromJsonAsync<TriggerReceipt>())!.Id == receipt.Id, "Trigger deduplication survives server restart");
    (await admin.DeleteAsync($"/api/integrations/tokens/{readerInfo.Id}")).EnsureSuccessStatusCode();
    Check((await read.GetAsync("/api/v1/cameras")).StatusCode == HttpStatusCode.Unauthorized, "Revocation takes effect on the next request");
    Console.WriteLine("All integration checks passed.");
}
finally
{
    await Stop(server);
    await Stop(fixture);
}
