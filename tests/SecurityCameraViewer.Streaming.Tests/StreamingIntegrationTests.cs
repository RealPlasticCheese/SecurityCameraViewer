using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Onvif;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class StreamingIntegrationTests
{
    public static async Task RunAsync(string[] args)
    {
        var assembly = Path.GetFullPath(Argument(args, "--server") ?? "src/SecurityCameraViewer.Web/bin/Debug/net10.0/SecurityCameraViewer.Web.dll");
        var executable = Path.GetFullPath(Argument(args, "--mediamtx") ?? "artifacts/web/mediamtx.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Install MediaMTX before running the integration suite.", executable);
        }

        var directory = Path.GetFullPath(Path.Combine("artifacts", "streaming-integration", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var ports = new HashSet<int>();
        int NextPort()
        {
            int port;
            do
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            while (!ports.Add(port));

            return port;
        }

        var upstreamPort = NextPort();
        var webPort = NextPort();
        var rtspPort = NextPort();
        int NextUdpPair()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var port = NextPort();
                if (port % 2 != 0 || port == 65535 || ports.Contains(port + 1))
                {
                    continue;
                }

                try
                {
                    using var rtp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                    using var rtcp = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1));
                    ports.Add(port + 1);
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            throw new IOException("No free RTP/RTCP UDP port pair was found.");
        }

        var rtpPort = NextUdpPair();
        var settings = new ViewerSettings
        {
            AutoConnect = false,
            Sources = [new CameraSource
            {
                Channel = 1, Name = "Relay fixture", MainStream = $"rtsp://127.0.0.1:{upstreamPort}/ip01/0",
                SubStream = $"rtsp://127.0.0.1:{upstreamPort}/ip01/1", Username = "viewer", UseDefaultPassword = true
            }, new CameraSource { Channel = 2, Name = "Disabled", MainStream = $"rtsp://127.0.0.1:{upstreamPort}/ip02/0", Enabled = false }]
        };
        var settingsPath = Path.Combine(directory, "settings.json");
        var sessionPath = Path.Combine(directory, "upstream-sessions.jsonl");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new AppSettings { Configurations = [new() { Connection = settings, Password = "local-only-test" }] }));
        Process? camera = null;
        Process? server = null;
        try
        {
            camera = Start(OperatingSystem.IsWindows() ? "python" : "python3", ["tests/rtsp_fixture.py"], new()
            {
                ["RTSP_FIXTURE_PORT"] = upstreamPort.ToString(),
                ["RTSP_FIXTURE_AUDIO_CHANNELS"] = "all",
                ["RTSP_FIXTURE_AUTH_QOP"] = "1",
                ["RTSP_FIXTURE_SESSION_LOG_FILE"] = sessionPath
            });
            await UntilAsync(async () => (await RtspProbe.DescribeAsync(settings, "local-only-test")).Success, "Upstream RTSP fixture");
            server = Start("dotnet", [assembly, "--urls", $"http://127.0.0.1:{webPort}", "--settings", settingsPath]);
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{webPort}"), Timeout = TimeSpan.FromSeconds(20) };
            await UntilAsync(async () =>
            {
                try
                {
                    return (await http.GetAsync("/api/streaming")).IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, "Streaming management endpoint");
            Check((await http.PostAsync("/onvif/device_service", SoapContent(new XElement(Device + "GetDeviceInformation")))).StatusCode == HttpStatusCode.NotFound,
                "Disabled installations expose no ONVIF device endpoint");
            var initial = await http.GetFromJsonAsync<JsonElement>("/api/streaming");
            var config = initial.GetProperty("settings").Deserialize<StreamingConfiguration>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })! with
            {
                Enabled = true,
                AdvertisedHost = "127.0.0.1",
                OnvifBaseUrl = $"http://127.0.0.1:{webPort}/",
                RtspPort = rtspPort,
                RtpPort = rtpPort,
                RtcpPort = rtpPort + 1,
                ApiPort = NextPort(),
                MediaMtxExecutable = executable,
                DiscoveryEnabled = false
            };
            var saved = await http.PutAsJsonAsync("/api/streaming", new StreamingSettingsRequest(config, "client-secret"));
            Check(saved.IsSuccessStatusCode, "Existing web server saves relay settings through the hamburger-menu API");
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/streaming")).GetProperty("status").GetProperty("running").GetBoolean(), "Managed MediaMTX readiness");
            var state = await http.GetFromJsonAsync<JsonElement>("/api/streaming");
            Check(state.GetProperty("cameras").GetArrayLength() == 1 && !state.ToString().Contains("client-secret") && !state.ToString().Contains("local-only-test"),
                "Management reports only active cameras and never returns credentials");
            var profileToken = state.GetProperty("cameras")[0].GetProperty("profiles")[0].GetProperty("token").GetString()!;
            var cameraId = settings.Sources[0].Id;
            var cameraAddress = $"http://127.0.0.1:{webPort}/onvif/cameras/{cameraId}/device_service";
            var relaySettings = new ViewerSettings
            {
                Sources = [new CameraSource
                {
                    Channel = 1, MainStream = $"rtsp://127.0.0.1:{rtspPort}/cameras/{cameraId}/main", Username = "viewer", UseDefaultPassword = true
                }]
            };
            var rtsp = await RtspProbe.DescribeAsync(relaySettings, "client-secret");
            Check(rtsp.Success && rtsp.Codec == "H264", "Real MediaMTX relays authenticated H.264 over a stable camera path");
            Check((await RtspProbe.DescribeAsync(relaySettings, "wrong-secret")).StatusCode == 401, "Real RTSP server rejects an incorrect client password");

            var inspector = new OnvifClient();
            var report = await inspector.InspectAsync(cameraAddress, "viewer", "client-secret", CancellationToken.None);
            Check(report.Manufacturer == "SecurityCameraViewer" && report.Profiles.Length == 4 && report.Profiles.All(p => p.StreamUri?.Contains($":{rtspPort}/cameras/{cameraId}/") == true),
                "Existing ONVIF client discovers Media and Media2 main/sub profiles through the virtual camera");
            Check(report.ClockOffsetSeconds is { } offset && Math.Abs(offset) < 5, "Wire ONVIF UTC bootstrap produces a usable clock offset");

            using var soapBasic = new HttpClient { BaseAddress = http.BaseAddress };
            soapBasic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("viewer:client-secret")));
            using var soap11 = await soapBasic.PostAsync("/onvif/media_service", SoapContent(new XElement(Media + "GetProfiles"), Soap11));
            Check(soap11.IsSuccessStatusCode && XDocument.Parse(await soap11.Content.ReadAsStringAsync()).Root?.Name == Soap11 + "Envelope", "SOAP 1.1 works over real HTTP endpoints");
            using var soapBad = await soapBasic.PostAsync("/onvif/device_service", SoapContent(new XElement(Device + "SystemReboot")));
            Check(soapBad.StatusCode == HttpStatusCode.BadRequest && (await soapBad.Content.ReadAsStringAsync()).Contains("ActionNotSupported"),
                "Unsupported controls return protocol faults on the wire");
            using var soapAnonymous = await http.PostAsync("/onvif/media_service", SoapContent(new XElement(Media + "GetProfiles")));
            Check(soapAnonymous.StatusCode == HttpStatusCode.Unauthorized && soapAnonymous.Headers.WwwAuthenticate.Count() == 2,
                "ONVIF clients receive both Basic and Digest challenges");
            using var digestHttp = new HttpClient(new HttpClientHandler { Credentials = new NetworkCredential("viewer", "client-secret") }) { BaseAddress = http.BaseAddress };
            using var digestResponse = await digestHttp.PostAsync("/onvif/media_service", SoapContent(new XElement(Media + "GetProfiles")));
            Check(digestResponse.IsSuccessStatusCode, "Standard .NET HTTP Digest negotiation interoperates with the service");

            var snapshotPath = $"/onvif/cameras/{cameraId}/snapshot/{profileToken}";
            using var snapshotResponse = await soapBasic.GetAsync(snapshotPath);
            var jpeg = await snapshotResponse.Content.ReadAsByteArrayAsync();
            Check(snapshotResponse.IsSuccessStatusCode && snapshotResponse.Content.Headers.ContentType?.MediaType == "image/jpeg" && jpeg.Length > 1000 && jpeg[0] == 0xff && jpeg[1] == 0xd8,
                "Snapshot endpoint decodes actual relay video while browser viewing is disconnected");
            Check((await http.GetAsync(snapshotPath)).StatusCode == HttpStatusCode.Unauthorized, "Snapshot endpoints require client authentication");
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/streaming")).GetProperty("cameras")[0].GetProperty("profiles")[0].GetProperty("media").GetProperty("width").GetInt32() == 640,
                "FFprobe learns actual camera profile metadata");
            var actual = await inspector.InspectAsync(cameraAddress, "viewer", "client-secret", CancellationToken.None);
            Check(actual.Profiles.All(p => p.Width == 640 && p.Height == 360), "ONVIF media descriptors reflect the actual decoded source resolution");

            var subscription = await soapBasic.PostAsync($"/onvif/cameras/{cameraId}/events_service", SoapContent(new XElement(Events + "CreatePullPointSubscription")));
            var subDocument = XDocument.Parse(await subscription.Content.ReadAsStringAsync());
            var pullPath = new Uri(subDocument.Descendants(Addressing + "Address").Single().Value).AbsolutePath;
            var pull = await soapBasic.PostAsync(pullPath, SoapContent(new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT0S"), new XElement(Events + "MessageLimit", 32))));
            Check(subscription.IsSuccessStatusCode && pull.IsSuccessStatusCode, "ONVIF clients create and pull event subscriptions over HTTP");

            await http.PostAsync("/api/connect", null);
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("cameras")[0].GetProperty("live").GetBoolean(),
                "Web viewer renders through the local relay");
            var recording = new RecordingSettings
            {
                Enabled = true,
                Directory = Path.Combine(directory, "recordings"),
                Cameras = [new CameraRecordingSettings { Channel = 1, DetectionSubstream = true }]
            };
            Check((await http.PostAsJsonAsync("/api/recording", recording)).IsSuccessStatusCode, "Recording workers can use the consolidated relay");
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/recording")).GetProperty("cameras")[0].GetProperty("bufferSeconds").GetDouble() > 0,
                "Recording pre-event compressed video buffer fills through the relay");
            await using (var external = new FfmpegSession(relaySettings, "client-secret", 1))
            {
                await UntilAsync(() => Task.FromResult(external.ConsumeFrame(_ => { }, out _)), "Independent RTSP client decodes relay video");
                await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/streaming")).GetProperty("status").GetProperty("readers").GetInt32() >= 4,
                    "Web, recording, motion and external clients share relay paths");
                var sessions = File.ReadLines(sessionPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                var closed = sessions.Where(s => s.GetProperty("type").GetString() == "closed").Select(s => s.GetProperty("connection").GetString()).ToHashSet();
                var active = sessions.Where(s => s.GetProperty("type").GetString() == "play" && !closed.Contains(s.GetProperty("connection").GetString())).ToArray();
                Check(active.Count(s => s.GetProperty("uri").GetString()!.TrimEnd('/').EndsWith("/ip01/0")) == 1 && active.Count(s => s.GetProperty("uri").GetString()!.TrimEnd('/').EndsWith("/ip01/1")) == 1,
                    "Multiple readers use exactly one upstream main connection and one upstream substream connection");
            }

            var observed = await soapBasic.PostAsync(pullPath, SoapContent(new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT0S"), new XElement(Events + "MessageLimit", 128))));
            Check(XDocument.Parse(await observed.Content.ReadAsStringAsync()).Descendants(Notification + "Topic").Any(),
                "Real recording events reach ONVIF PullPoint clients");
            await http.PostAsJsonAsync("/api/recording", recording with
            {
                Enabled = false
            });

            await http.PostAsync("/api/disconnect", null);
            Check((await RtspProbe.DescribeAsync(relaySettings, "client-secret")).Success, "Browser Disconnect leaves the consolidated service running");
            var setup = await http.PostAsync("/api/integrations/setup", null);
            var setupData = await setup.Content.ReadFromJsonAsync<JsonElement>();
            if (!setup.IsSuccessStatusCode)
            {
                throw new Exception("Authentication setup failed: " + setupData);
            }

            var admin = setupData.GetProperty("token").GetString()!;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            var tokenResponse = await http.PostAsJsonAsync("/api/integrations/tokens", new
            {
                name = "Reader",
                scopes = new[] { "read" }
            });
            var reader = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            using var readHttp = new HttpClient { BaseAddress = http.BaseAddress };
            readHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reader);
            Check((await readHttp.GetAsync("/api/streaming")).StatusCode == HttpStatusCode.Forbidden, "Scoped read-only API tokens cannot administer or inspect streaming configuration");
            Check((await soapBasic.PostAsync("/onvif/device_service", SoapContent(new XElement(Device + "GetDeviceInformation")))).IsSuccessStatusCode,
                "Enabling browser authentication preserves independent ONVIF client access");

            saved = await http.PutAsJsonAsync("/api/streaming", new StreamingSettingsRequest(config with
            {
                Enabled = false
            }, null));
            Check(saved.IsSuccessStatusCode, "Administrator can disable the service");
            await UntilAsync(async () => !(await http.GetFromJsonAsync<JsonElement>("/api/streaming")).GetProperty("status").GetProperty("running").GetBoolean(), "Relay shutdown");
            Check((await soapBasic.PostAsync("/onvif/device_service", SoapContent(new XElement(Device + "GetDeviceInformation")))).StatusCode == HttpStatusCode.NotFound &&
                !File.Exists(Path.Combine(directory, "streaming-runtime.json")), "Disabling removes ONVIF access and runtime camera credentials");
        }
        finally
        {
            await StopAsync(server);
            await StopAsync(camera);
        }
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static StringContent SoapContent(XElement operation, XNamespace? soap = null) => new(
        new XDocument(new XElement((soap ?? Soap12) + "Envelope", new XElement((soap ?? Soap12) + "Body", operation))).ToString(), Encoding.UTF8,
        soap == Soap11 ? "text/xml" : "application/soap+xml");

    private static Process Start(string executable, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var pair in environment ?? [])
        {
            info.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Error.WriteLine("Test child: " + eventArgs.Data);
            }
        };
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

    private static async Task UntilAsync(Func<Task<bool>> predicate, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!await predicate())
        {
            if (watch.Elapsed.TotalSeconds > 40)
            {
                throw new Exception("Timed out: " + name);
            }

            await Task.Delay(100);
        }
    }
}
