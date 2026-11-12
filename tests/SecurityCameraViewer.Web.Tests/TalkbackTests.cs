using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class TalkbackTests
{
    public static async Task RunAsync(string assembly)
    {
        await HubTestsAsync();
        var directory = Path.GetFullPath(Path.Combine("artifacts", "talkback-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var rtspPort = Port();
        var webPort = Port();
        var logPath = Path.Combine(directory, "speaker.jsonl");
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = new ViewerSettings
        {
            AutoConnect = true,
            Sources = [.. Enumerable.Range(1, 2).Select(channel => new CameraSource
            {
                Channel = channel, MainStream = $"rtsp://127.0.0.1:{rtspPort}/ip{channel:00}/0",
                Username = "viewer", UseDefaultPassword = true
            })]
        };
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new AppSettings { Configurations = [new() { Connection = settings, Password = "local-only-test" }] }));
        Process? fixture = null;
        Process? server = null;
        try
        {
            fixture = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
            {
                ["RTSP_FIXTURE_PORT"] = rtspPort.ToString(),
                ["RTSP_FIXTURE_TALKBACK_CHANNELS"] = "1,3,4,5,6,7",
                ["RTSP_FIXTURE_TALKBACK_MODES"] = "4=foreign,5=unsupported,6=ignored,7=udp",
                ["RTSP_FIXTURE_TALKBACK_LOG_FILE"] = logPath,
                ["RTSP_FIXTURE_AUTH_QOP"] = "1"
            });
            await UntilAsync(async () => (await RtspProbe.DescribeAsync(settings, "local-only-test", 1)).Success,
                "Digest-qop RTSP talkback fixture is ready");
            var source = new RtspAudioBackchannel();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var capability = await source.ProbeAsync(settings, "local-only-test", 1, deadline.Token);
            Check(capability.Supported && capability.Codec == "PCMU", "Explicit PCMU speaker track and TCP transport negotiate successfully");
            Check(!ReadLog(logPath).Any(record => record.GetProperty("type").GetString() == "rtp"),
                "Capability discovery sends no audio and never starts PLAY");
            foreach (var (channel, expected) in new[] { (2, "unsupported"), (4, "unavailable"), (5, "unsupported"), (6, "unsupported"), (7, "unavailable") })
            {
                var variant = settings with
                {
                    Sources = [settings.Sources[0] with { Channel = channel, MainStream = $"rtsp://127.0.0.1:{rtspPort}/ip{channel:00}/0" }]
                };
                var result = await source.ProbeAsync(variant, "local-only-test", channel, deadline.Token);
                Check(!result.Supported && result.Status == expected, $"Channel {channel}: microphone-only, foreign controls, unsupported codecs, ignored Require and UDP-only responses cannot enable talkback");
            }
            await VerifyWireAudioAsync(source, settings, 1, logPath, directory, "PCMU");
            var aLaw = settings with
            {
                Sources = [settings.Sources[0] with { Channel = 3, MainStream = $"rtsp://127.0.0.1:{rtspPort}/ip03/0" }]
            };
            await VerifyWireAudioAsync(source, aLaw, 3, logPath, directory, "PCMA");
            server = Start("dotnet", [Path.GetFullPath(assembly), "--urls", $"http://127.0.0.1:{webPort}",
                "--settings", settingsPath, "--preview-on-demand", "false"]);
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{webPort}") };
            await UntilAsync(async () =>
            {
                try
                {
                    var state = await http.GetFromJsonAsync<JsonElement>("/api/state");
                    return state.GetProperty("cameras").EnumerateArray().Count(camera => camera.GetProperty("live").GetBoolean()) == 2;
                }
                catch (HttpRequestException) { return false; }
            }, "Web talkback host has fresh camera video");
            var supported = await http.GetFromJsonAsync<JsonElement>("/api/cameras/1/talkback");
            var unsupported = await http.GetFromJsonAsync<JsonElement>("/api/cameras/2/talkback");
            Check(supported.GetProperty("supported").GetBoolean() && supported.GetProperty("canTalk").GetBoolean() &&
                !unsupported.GetProperty("supported").GetBoolean() && !supported.ToString().Contains("local-only-test"),
                "Capability API gates controls without exposing RTSP credentials or addresses");
            using (var speaker = await ConnectAsync(webPort, 1))
            {
                await ReadyAsync(speaker);
                using var competitor = new HttpRequestMessage(HttpMethod.Get, "/api/cameras/1/talk");
                competitor.Headers.Add("Connection", "Upgrade");
                competitor.Headers.Add("Upgrade", "websocket");
                competitor.Headers.Add("Sec-WebSocket-Version", "13");
                competitor.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
                Check((await http.SendAsync(competitor)).StatusCode == HttpStatusCode.Conflict, "A second browser cannot take over an active camera speaker");
                var pcm = Tone();
                for (var index = 0; index < 12; index++)
                {
                    await speaker.SendAsync(pcm, WebSocketMessageType.Binary, true, CancellationToken.None);
                    await Task.Delay(20);
                }
                await ClosedAsync(speaker);
                Check(true, "An idle microphone sender is stopped within the server watchdog");
            }
            await SpeakerFreeAsync(http);
            using (var malformed = await ConnectAsync(webPort, 1))
            {
                await ReadyAsync(malformed);
                await malformed.SendAsync(new byte[321], WebSocketMessageType.Binary, true, CancellationToken.None);
                await ClosedAsync(malformed);
                Check(true, "Oversized microphone messages close the session instead of growing buffers");
            }
            await SpeakerFreeAsync(http);
            using (var changed = await ConnectAsync(webPort, 1))
            {
                await ReadyAsync(changed);
                (await http.PostAsync("/api/quality", null)).EnsureSuccessStatusCode();
                await ClosedAsync(changed);
                Check(true, "Connection-generation changes stop the camera speaker lease");
            }
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("cameras")
                .EnumerateArray().Any(camera => camera.GetProperty("channel").GetInt32() == 1 && camera.GetProperty("live").GetBoolean()), "Video resumes after a quality change");
            await http.GetFromJsonAsync<JsonElement>("/api/cameras/1/talkback");
            using (var limited = await ConnectAsync(webPort, 1))
            {
                await ReadyAsync(limited);
                using var sending = new CancellationTokenSource();
                var elapsed = Stopwatch.StartNew();
                var pump = Task.Run(async () =>
                {
                    try
                    {
                        var index = 0;
                        while (!sending.IsCancellationRequested)
                        {
                            var wait = index * .02 - elapsed.Elapsed.TotalSeconds;
                            if (wait > 0)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(wait), sending.Token);
                            }

                            await limited.SendAsync(Tone(index++), WebSocketMessageType.Binary, true, sending.Token);
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                });
                await ClosedAsync(limited, seconds: 35);
                await sending.CancelAsync();
                await pump;
                Check(elapsed.Elapsed.TotalSeconds is >= 27 and < 34, "The server independently ends a continuously transmitting press at thirty seconds");
            }
            await SpeakerFreeAsync(http);
            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/cameras/1/talk");
            foreign.Headers.Add("Origin", "http://other.example");
            Check((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "Talkback retains same-origin protection");
            (await http.PostAsJsonAsync("/api/integrations/setup", new
            {
            })).EnsureSuccessStatusCode();
            var read = await CreateTokenAsync(http, "reader", ["read"]);
            var control = await CreateTokenAsync(http, "speaker", ["read", "control"]);
            using var reader = new HttpClient { BaseAddress = http.BaseAddress };
            reader.DefaultRequestHeaders.Authorization = new("Bearer", read.GetProperty("token").GetString());
            Check((await reader.GetAsync("/api/cameras/1/talk")).StatusCode == HttpStatusCode.Forbidden &&
                !(await reader.GetFromJsonAsync<JsonElement>("/api/cameras/1/talkback")).GetProperty("canTalk").GetBoolean(),
                "Read-only users can inspect capabilities but cannot send microphone audio");
            using (var revoked = await ConnectAsync(webPort, 1, control.GetProperty("token").GetString()))
            {
                await ReadyAsync(revoked);
                (await http.DeleteAsync("/api/integrations/tokens/" + control.GetProperty("details").GetProperty("id").GetString())).EnsureSuccessStatusCode();
                await ClosedAsync(revoked);
                Check(true, "Revoking control access stops a speaker lease while waiting for microphone frames");
            }
            using var anonymous = new HttpClient { BaseAddress = http.BaseAddress };
            Check((await anonymous.GetAsync("/api/cameras/1/talk")).StatusCode == HttpStatusCode.Unauthorized, "Authenticated installations reject anonymous talkback");
            Check(!(await http.GetFromJsonAsync<JsonElement>("/api/recording")).GetProperty("settings").GetProperty("enabled").GetBoolean(),
                "Talking does not enable or configure recording");
        }
        finally
        {
            await StopAsync(server);
            await StopAsync(fixture);
        }
    }

    private static async Task VerifyWireAudioAsync(ITalkbackSource source, ViewerSettings settings, int channel, string logPath, string directory, string codec)
    {
        var offset = ReadLog(logPath).Count;
        await using (var session = await source.OpenAsync(settings, "local-only-test", channel, CancellationToken.None))
        {
            Check(session.Codec == codec, "Static PCMU and dynamic PCMA payload mappings select the right encoder");
            for (var index = 0; index < 60; index++)
            {
                await session.SendAsync(Tone(index), CancellationToken.None);
            }

            await session.KeepAliveAsync(CancellationToken.None);
        }
        var records = ReadLog(logPath).Skip(offset).ToArray();
        var packets = records.Where(record => record.GetProperty("type").GetString() == "rtp")
            .Select(record => Convert.FromBase64String(record.GetProperty("data").GetString()!)).ToArray();
        Check(packets.Length == 60 && packets.All(packet => packet.Length == 172 && packet[0] == 0x80 && (packet[1] & 0x7f) == (codec == "PCMU" ? 0 : 98)) &&
            records.Where(record => record.GetProperty("type").GetString() == "rtp").All(record => record.GetProperty("active").GetBoolean()),
            "Interleaved RTP sends exactly one 160-sample payload per frame only after PLAY");
        for (var index = 1; index < packets.Length; index++)
        {
            Check(unchecked((ushort)(BinaryPrimitives.ReadUInt16BigEndian(packets[index].AsSpan(2)) - BinaryPrimitives.ReadUInt16BigEndian(packets[index - 1].AsSpan(2)))) == 1 &&
                unchecked(BinaryPrimitives.ReadUInt32BigEndian(packets[index].AsSpan(4)) - BinaryPrimitives.ReadUInt32BigEndian(packets[index - 1].AsSpan(4))) == 160 &&
                packets[index].AsSpan(8, 4).SequenceEqual(packets[0].AsSpan(8, 4)), "RTP sequence, 8 kHz timestamps and SSRC remain consistent", quiet: true);
        }
        Check(records.Any(record => record.GetProperty("type").GetString() == "rtcp") && records.Any(record => record.GetProperty("type").GetString() == "teardown"),
            "Backchannel sends RTCP sender reports and tears down the speaker session");
        var path = Path.Combine(directory, codec + ".raw");
        await File.WriteAllBytesAsync(path, packets.SelectMany(packet => packet.Skip(12)).ToArray());
        var process = new SecurityCameraViewer.Recording.FfmpegProcess("ffmpeg");
        var samples = new MemoryStream();
        await process.RunAsync(["-f", codec == "PCMU" ? "mulaw" : "alaw", "-ar", "8000", "-ac", "1", "-i", path,
            "-f", "s16le", "pipe:1"], null, (stream, token) => stream.CopyToAsync(samples, token), CancellationToken.None);
        var decoded = samples.ToArray();
        Check(decoded.Length == 60 * 320 && decoded.AsSpan().ContainsAnyExcept((byte)0), "Real transmitted G.711 audio decodes to the expected non-silent duration");
        for (var index = 0; index < decoded.Length / 2; index++)
        {
            var expected = (short)(4000 * Math.Sin(2 * Math.PI * 440 * index / 8000));
            Check(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(index * 2)) - expected) <= 128,
                "An independent FFmpeg decoder recovers the transmitted waveform within G.711 quantization error", quiet: true);
        }
    }

    private static async Task HubTestsAsync()
    {
        var source = new FakeSource();
        var hub = new TalkbackHub(source);
        var settings = new ViewerSettings();
        var probes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => hub.ProbeAsync(settings, "", 1, CancellationToken.None)));
        Check(source.Probes == 1 && probes.All(probe => probe.Supported), "Concurrent capability checks share a transient probe");
        var lease = hub.Reserve(settings, "", 1, CancellationToken.None);
        await lease.Session;
        try
        {
            hub.Reserve(settings, "", 1, CancellationToken.None);
            throw new Exception("Duplicate speaker accepted");
        }
        catch (InvalidOperationException) { }
        Check((await hub.ProbeAsync(settings, "", 1, CancellationToken.None)).Status == "busy" && source.Probes == 1,
            "Capability reads never negotiate over an occupied speaker");
        await hub.StopAsync();
        await lease.DisposeAsync();
        Check(lease.Token.IsCancellationRequested && source.Closed == 1, "Viewer shutdown cancels and releases each speaker exactly once");
        await hub.ProbeAsync(settings, "", 1, CancellationToken.None);
        await using var replacement = hub.Reserve(settings, "", 1, CancellationToken.None);
        await replacement.Session;
        await hub.StopAsync();
        Check(source.Closed == 2, "Speaker can be reserved again after a connection generation changes");
    }

    private static byte[] Tone(int frame = 0)
    {
        var pcm = new byte[320];
        for (var index = 0; index < 160; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2),
            (short)(4000 * Math.Sin(2 * Math.PI * 440 * (frame * 160 + index) / 8000)));
        }

        return pcm;
    }
    private static List<JsonElement> ReadLog(string path) => !File.Exists(path) ? [] :
        [.. File.ReadLines(path).Where(line => line.EndsWith('}')).Select(line => JsonSerializer.Deserialize<JsonElement>(line))];
    private static async Task<JsonElement> CreateTokenAsync(HttpClient http, string name, string[] scopes)
    {
        var response = await http.PostAsJsonAsync("/api/integrations/tokens", new TokenRequest(name, scopes, null));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task SpeakerFreeAsync(HttpClient http)
        => await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/cameras/1/talkback"))
            .GetProperty("status").GetString() != "busy", "Speaker teardown releases the exclusive lease before another press");
    private static async Task<ClientWebSocket> ConnectAsync(int port, int channel, string? token = null)
    {
        var socket = new ClientWebSocket();
        if (token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new($"ws://127.0.0.1:{port}/api/cameras/{channel}/talk"), deadline.Token);
        return socket;
    }
    private static async Task ReadyAsync(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bytes = new byte[1024];
        var received = await socket.ReceiveAsync(bytes, deadline.Token);
        Check(received.MessageType == WebSocketMessageType.Text && JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, received.Count))
            .GetProperty("type").GetString() == "ready", "Talkback opens with a fixed PCM format handshake");
    }
    private static async Task ClosedAsync(ClientWebSocket socket, int seconds = 5)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            while ((await socket.ReceiveAsync(new byte[1024], deadline.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException) { }
    }
    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static Process Start(string executable, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var item in environment ?? [])
        {
            info.Environment[item.Key] = item.Value;
        }

        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }
    private static async Task StopAsync(Process? process)
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
    private static async Task UntilAsync(Func<Task<bool>> condition, string message)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!await condition())
        {
            await Task.Delay(100, deadline.Token);
        }

        Check(true, message);
    }
    private static void Check(bool value, string message, bool quiet = false)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + message);
        }

        if (!quiet)
        {
            Console.WriteLine("PASS: " + message);
        }
    }
    private sealed class FakeSource : ITalkbackSource
    {
        public int Probes;
        public int Closed;
        public async Task<TalkbackCapability> ProbeAsync(ViewerSettings settings, string password, int channel, CancellationToken token)
        {
            Interlocked.Increment(ref Probes);
            await Task.Delay(20, token);
            return new(true, "supported", "Ready", "PCMU");
        }
        public Task<ITalkbackSession> OpenAsync(ViewerSettings settings, string password, int channel, CancellationToken token)
            => Task.FromResult<ITalkbackSession>(new FakeSession(this));
        private sealed class FakeSession(FakeSource source) : ITalkbackSession
        {
            public string Codec => "PCMU";
            public Task SendAsync(ReadOnlyMemory<byte> pcm, CancellationToken token) => Task.CompletedTask;
            public Task KeepAliveAsync(CancellationToken token) => Task.CompletedTask;
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref source.Closed);
                return ValueTask.CompletedTask;
            }
        }
    }
}
