using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

if (Environment.GetEnvironmentVariable("SCV_BROWSER_TEST_CAPTURE") is not null && args.Length == 1 && args[0].StartsWith("--app=", StringComparison.Ordinal))
{
    await BrowserLaunchTests.CaptureAsync(args);
    return;
}

await CameraAspectRatioTests.RunAsync();
if (args.Contains("--aspect-ratio-only"))
{
    return;
}

if (args.Contains("--rewind-save-only"))
{
    await RewindRecordingTests.RunAsync();
    return;
}

if (args.Contains("--rewind-save-integration-only"))
{
    await LiveMediaTests.RunAsync(args.FirstOrDefault(argument => argument != "--rewind-save-integration-only") ?? "artifacts/rewind-web/SecurityCameraViewer.Web.dll");
    return;
}

await CameraSynchronizationTests.RunAsync();
if (args.Contains("--camera-sync-only"))
{
    return;
}
await BrowserLaunchTests.RunAsync();
if (args.Contains("--browser-launch-only"))
{
    await BrowserLaunchTests.RunIntegrationAsync(args.FirstOrDefault(argument => argument != "--browser-launch-only") ?? "artifacts/web/SecurityCameraViewer.Web.dll");
    return;
}

if (args.Contains("--enhancements-only"))
{
    await AppSettingsTests.RunAsync();
    await CameraEnhancementTests.RunAsync();
    return;
}

if (args.Contains("--audio-metadata-only"))
{
    await AudioMetadataTests.RunAsync();
    return;
}

await CameraRetryTests.RunAsync();
if (args.Contains("--camera-retry-only"))
{
    return;
}

if (args.Contains("--preview-snapshots-only"))
{
    await PreviewSnapshotTests.RunAsync();
    return;
}

await AudioMetadataTests.RunAsync();
await CameraPlaybackTests.RunAsync();
await DetectionSynchronizationTests.RunAsync();
await AppSettingsTests.RunAsync();
await CameraEnhancementTests.RunAsync();
await LivePerformanceTests.CharacterizeAsync();
await LivePerformanceTests.RunAsync();
await PreviewDemandTests.RunAsync();
FfmpegResourceTests.Run();
if (args.Contains("--live-performance-only"))
{
    return;
}
if (args.Contains("--passthrough-files-only"))
{
    await LivePassthroughFileTests.RunAsync();
    await PreviewSnapshotTests.RunAsync();
    return;
}
await LiveRewindTests.RunAsync();
await RewindWindowTests.RunAsync();
await RewindRecordingTests.RunAsync();
if (args.Contains("--rewind-only"))
{
    return;
}
if (args.Contains("--rewind-integration-only"))
{
    await LiveMediaTests.RunAsync(args.FirstOrDefault(argument => argument != "--rewind-integration-only") ?? "artifacts/rewind-web/SecurityCameraViewer.Web.dll");
    return;
}
await CameraCapacityTests.RunAsync();
if (args.Contains("--settings-only"))
{
    return;
}

if (args.Contains("--recording-filters-only"))
{
    await RecordingFilterTests.RunAsync();
    return;
}
if (args.Contains("--battery-only"))
{
    await BatteryCameraTests.RunAsync();
    return;
}

await CameraFailureTests.RunAsync();
if (args.Contains("--camera-failures-only"))
{
    return;
}

if (args.Contains("--talkback-only"))
{
    await TalkbackTests.RunAsync(args.FirstOrDefault(argument => argument != "--talkback-only") ?? "artifacts/talkback-web/SecurityCameraViewer.Web.dll");
    return;
}

if (args.Contains("--live-only"))
{
    await LiveMediaTests.RunAsync(args.FirstOrDefault(argument => argument != "--live-only") ?? "artifacts/live-web/SecurityCameraViewer.Web.dll");
    return;
}

await VideoTimeoutTests.RunAsync();
if (args.Contains("--video-timeout-only"))
{
    return;
}

await PasswordVisibilityTests.RunAsync();
if (args.Contains("--password-only"))
{
    return;
}

await BatteryCameraTests.RunAsync();
await RecordingFilterTests.RunAsync();
await LiveMediaTests.RunAsync(args.FirstOrDefault() ?? "artifacts/live-web/SecurityCameraViewer.Web.dll");
await LivePassthroughFileTests.RunAsync();
await PreviewSnapshotTests.RunAsync();
await TalkbackTests.RunAsync(args.FirstOrDefault() ?? "artifacts/talkback-web/SecurityCameraViewer.Web.dll");

static void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception("FAIL: " + name);
    }

    Console.WriteLine("PASS: " + name);
}
static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
static async Task Until(Func<Task<bool>> condition, string name, int seconds = 30)
{
    var timer = Stopwatch.StartNew();
    while (!await condition())
    {
        if (timer.Elapsed.TotalSeconds > seconds)
        {
            throw new Exception("Timed out: " + name);
        }

        await Task.Delay(100);
    }
}
static async Task Kill(Process? process)
{
    if (process is null)
    {
        return;
    }

    if (!process.HasExited)
    {
        process.Kill(true);
    }

    await process.WaitForExitAsync();
    process.Dispose();
}
static Process Start(string executable, string[] arguments, Dictionary<string, string>? environment = null)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
static async Task ReadFourCameras(ClientWebSocket socket)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var channels = new HashSet<int>();
    var buffer = new byte[65536];
    while (channels.Count < 4)
    {
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new Exception("Video socket closed unexpectedly");
            }

            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        if (result.MessageType == WebSocketMessageType.Text)
        {
            await socket.SendAsync("ok"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            continue;
        }
        var bytes = message.ToArray();
        Check(bytes.Length > 100 && bytes[13] == 0xff && bytes[14] == 0xd8 && bytes[^2] == 0xff && bytes[^1] == 0xd9 &&
            System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(5)) >= 0,
            "WebSocket transports a complete JPEG with its channel, generation and frame age");
        channels.Add(bytes[0]);
    }
}

var root = Environment.CurrentDirectory;
if (!File.Exists(Path.Combine(root, "SecurityCameraViewer.slnx")))
{
    throw new Exception("Run these tests from the repository root.");
}
// An optional publish path lets tests run without replacing an active viewer's files.
var serverAssembly = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/web/SecurityCameraViewer.Web.dll");
Directory.CreateDirectory("artifacts");
var testDirectory = Path.Combine(root, "artifacts", "web-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
var settingsPath = Path.Combine(testDirectory, "settings.json");
var pausePath = Path.Combine(testDirectory, "pause");
var rtspPort = FreePort();
var webPort = FreePort();
var webHttp2Port = FreePort();
var settings = new ViewerSettings
{
    Host = "127.0.0.1",
    RtspPort = rtspPort,
    Username = "viewer",
    AutoConnect = false,
    VideoTimeoutSeconds = 2
};
await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new AppSettings { Configurations = [new() { Connection = settings, Password = "local-only-test" }] }));
Process? fixture = null, server = null;
try
{
    // Deliberately fragment every JPEG marker, and leave the last frame incomplete.
    var received = new List<byte[]>();
    await JpegFrames.ReadAsync(new FragmentedStream([0, 0xff, 0xd8, 1, 2, 0xff, 0xd9, 0xff, 0xd8, 3]), received.Add, CancellationToken.None);
    Check(received.Count == 1 && received[0].SequenceEqual(new byte[] { 0xff, 0xd8, 1, 2, 0xff, 0xd9 }), "Fragmented JPEG markers are preserved and incomplete frames are discarded");
    var huge = new byte[JpegFrames.MaximumFrameBytes + 1];
    huge[0] = 0xff;
    huge[1] = 0xd8;
    try
    {
        await JpegFrames.ReadAsync(new MemoryStream(huge), _ => { }, CancellationToken.None);
        throw new Exception("Missing size limit");
    }
    catch (InvalidDataException)
    {
        Check(true, "Oversized frames cannot grow memory without a bound");
    }

    fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"],
        new()
        {
            ["RTSP_FIXTURE_PORT"] = rtspPort.ToString(),
            ["RTSP_FIXTURE_PAUSE_FILE"] = pausePath
        });
    await Until(async () => (await RtspProbe.DescribeAsync(settings, "local-only-test")).Success, "RTSP fixture startup");
    server = Start("dotnet", [serverAssembly,
        "--Kestrel:Endpoints:Http1:Url", $"http://127.0.0.1:{webPort}", "--Kestrel:Endpoints:Http1:Protocols", "Http1",
        "--Kestrel:Endpoints:Http2:Url", $"http://127.0.0.1:{webHttp2Port}", "--Kestrel:Endpoints:Http2:Protocols", "Http2",
        "--settings", settingsPath, "--preview-on-demand", "false"]);
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{webPort}"), Timeout = TimeSpan.FromSeconds(15) };
    await Until(async () => { try { return (await http.GetAsync("/api/state")).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "web startup");
    async Task<JsonElement> State() => (await http.GetFromJsonAsync<JsonElement>("/api/state"));
    async Task<bool> AllLive()
    {
        var s = await State();
        return s.GetProperty("cameras").GetArrayLength() == 4 && s.GetProperty("cameras").EnumerateArray().All(c => c.GetProperty("live").GetBoolean());
    }
    async Task Post(string path) => (await http.PostAsync(path, null)).EnsureSuccessStatusCode();
    var html = await http.GetStringAsync("/");
    Check(html.Contains("SECURITY CAMERA VIEWER") && (await http.GetAsync("/app.js")).IsSuccessStatusCode, "Published server serves browser assets from a different working directory");
    var initial = await State();
    Check(!initial.GetProperty("connected").GetBoolean() && initial.GetProperty("hasPassword").GetBoolean(), "Auto-connect preference and saved password are respected");
    Check(!initial.ToString().Contains("local-only-test"), "State API does not expose the recorder password");
    using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/connect");
    crossOrigin.Headers.Add("Origin", "http://unrelated.example");
    Check((await http.SendAsync(crossOrigin)).StatusCode == HttpStatusCode.Forbidden, "Other websites cannot change the viewer connection");
    var invalid = await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(settings with
    {
        ChannelCount = 0
    }, null));
    Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid settings are rejected before changing the connection");
    var rejected = await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(settings, "incorrect-test-password"));
    rejected.EnsureSuccessStatusCode();
    var rejectedState = await State();
    Check(!rejectedState.GetProperty("connected").GetBoolean() && rejectedState.GetProperty("notice").GetString()!.Contains("login rejected"), "A rejected login stops before starting four decoders");
    (await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(settings, "local-only-test"))).EnsureSuccessStatusCode();
    await Until(AllLive, "four web cameras live");
    Check(true, "Four authenticated cameras decode in the web server");
    using (var socket = new ClientWebSocket())
    using (var other = new ClientWebSocket())
    using (var http2Socket = new ClientWebSocket())
    using (var http2Invoker = new HttpMessageInvoker(new SocketsHttpHandler()))
    {
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{webPort}/api/video"), CancellationToken.None);
        await other.ConnectAsync(new Uri($"ws://127.0.0.1:{webPort}/api/video"), CancellationToken.None);
        await Task.WhenAll(ReadFourCameras(socket), ReadFourCameras(other));
        http2Socket.Options.HttpVersion = HttpVersion.Version20;
        http2Socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        http2Socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{webHttp2Port}");
        using var http2Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await http2Socket.ConnectAsync(new Uri($"ws://127.0.0.1:{webHttp2Port}/api/video"), http2Invoker, http2Deadline.Token);
        await ReadFourCameras(http2Socket);
        Check(true, "HTTP/2 CONNECT WebSockets deliver all four camera streams");
        // The first socket stops acknowledging, but the shared decoders must keep advancing.
        await Task.Delay(11000);
        Check(await AllLive(), "A slow viewer cannot stall shared camera playback");
    }
    await File.WriteAllTextAsync(pausePath, "");
    await Until(async () => (await State()).GetProperty("cameras").EnumerateArray().All(c => !c.GetProperty("live").GetBoolean() && c.GetProperty("frameAgeSeconds").GetDouble() >= 4), "stale frames on open RTSP sockets", 15);
    Check(true, "Stalled input cannot manufacture duplicate LIVE frames");
    using (var expiredSocket = new ClientWebSocket())
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        await expiredSocket.ConnectAsync(new Uri($"ws://127.0.0.1:{webPort}/api/video"), timeout.Token);
        var buffer = new byte[65536];
        for (var tick = 0; tick < 3; tick++)
        {
            var message = await expiredSocket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Check(message.MessageType == WebSocketMessageType.Text && message.EndOfMessage,
                "A new browser receives no expired cached video");
            await expiredSocket.SendAsync("ok"u8.ToArray().AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        }
    }
    var snapshot = await http.GetByteArrayAsync("/api/cameras/1/snapshot");
    Check(snapshot[0] == 0xff && snapshot[1] == 0xd8, "Video timeout preserves snapshot downloads");
    File.Delete(pausePath);
    await Until(AllLive, "frames resuming", 15);
    await Kill(fixture);
    fixture = null;
    await Until(async () => !(await AllLive()), "recorder disconnect", 15);
    Check((await State()).GetProperty("cameras").EnumerateArray().All(c => c.GetProperty("frameAgeSeconds").ValueKind == JsonValueKind.Number), "Last frames remain available through recorder failure");
    fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
    {
        ["RTSP_FIXTURE_PORT"] = rtspPort.ToString()
    });
    await Until(AllLive, "automatic recovery", 40);
    Check(true, "All four cameras recover after the recorder restarts");
    await Post("/api/quality");
    await Until(AllLive, "quality reconnect");
    Check((await State()).GetProperty("settings").GetProperty("substream").GetBoolean(), "Quality toggles and resumes playback");
    await Post("/api/disconnect");
    await Task.Delay(2500);
    Check(!(await State()).GetProperty("connected").GetBoolean(), "Disconnect stops cameras and automatic retries");
    var saved = await File.ReadAllTextAsync(settingsPath);
    Check(saved.Contains("Substream") && saved.Contains("local-only-test") && !saved.Contains("incorrect-test-password"), "Settings and the saved password persist without retaining an incorrect probe password");

    // Exercise the actual hosted service shutdown during playback and a blocked startup probe.
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["settings"] = settingsPath
        ,
        ["preview-on-demand"] = "false"
    }).Build();
    using (var service = new ViewerService(new WebSettingsStore(new JsonAppSettingsStore(configuration)), configuration))
    {
        await service.ConnectAsync();
        await Until(() => Task.FromResult(service.Cameras.All(c => c.State.Live)), "service playback");
        var cameras = service.Cameras;
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check(service.Cameras.Length == 0 && cameras.All(c => !c.State.Live), "Hosted shutdown awaits every decoder");
    }
    using var hanging = new TcpListener(IPAddress.Loopback, 0);
    hanging.Start();
    await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new AppSettings
    {
        Configurations = [new() { Password = "local-only-test", Connection = settings with
    {
        RtspPort = ((IPEndPoint)hanging.LocalEndpoint).Port
    } }]
    }));
    using (var service = new ViewerService(new WebSettingsStore(new JsonAppSettingsStore(configuration)), configuration))
    {
        var connecting = service.ConnectAsync();
        using var accepted = await hanging.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await connecting;
        }
        catch (OperationCanceledException)
        {
        }
        Check(service.Cameras.Length == 0, "Shutdown cancels an in-flight RTSP probe");
    }
    Console.WriteLine("All web server regression checks passed.");
}
finally
{
    await Kill(server);
    await Kill(fixture);
    // Only this test's explicitly named files are removed; retain the directory for diagnostics.
    File.Delete(pausePath);
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
}
