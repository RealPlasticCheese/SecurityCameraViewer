using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class LiveMediaTests
{
    public static async Task RunAsync(string assembly)
    {
        await SharedSessionTestsAsync();
        var directory = Path.GetFullPath(Path.Combine("artifacts", "live-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var rtspPort = Port();
        var webPort = Port();
        var sleepPath = Path.Combine(directory, "sleep");
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = new ViewerSettings
        {
            AutoConnect = true,
            ChannelCount = 2,
            Sources = [.. Enumerable.Range(1, 2).Select(channel => new CameraSource
            {
                Channel = channel,
                Name = "Live test " + channel,
                MainStream = $"rtsp://127.0.0.1:{rtspPort}/ip{channel:00}/0",
                Username = "viewer",
                UseDefaultPassword = true
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
                ["RTSP_FIXTURE_SLEEP_FILE"] = sleepPath,
                ["RTSP_FIXTURE_AUDIO_CHANNELS"] = "1"
            });
            await UntilAsync(async () => (await RtspProbe.DescribeAsync(settings, "local-only-test", 1)).Success,
                "Live RTSP fixture starts");
            var ambiguous = new ProbeResult(true, 200, "Ambiguous audio")
            {
                AudioVerificationRequired = true, AudioCodec = "PCMU"
            };
            var verification = new CameraAudioVerification(new FfmpegProcess("ffmpeg"));
            var verified = await verification.VerifyAsync(ambiguous, settings, "local-only-test", 1, CancellationToken.None);
            Check(verified is { HasAudio: true, AudioVerificationRequired: false },
                "Real RTSP G.711 samples verify an ambiguous microphone");
            var unverified = await verification.VerifyAsync(ambiguous, settings, "local-only-test", 2, CancellationToken.None);
            Check(unverified is { HasAudio: null, AudioVerificationRequired: true },
                "A real video-only stream never verifies an ambiguous microphone");
            server = Start("dotnet", [Path.GetFullPath(assembly), "--urls", $"http://127.0.0.1:{webPort}",
                "--settings", settingsPath]);
            using var http = new HttpClient
            {
                BaseAddress = new($"http://127.0.0.1:{webPort}"),
                Timeout = TimeSpan.FromSeconds(15)
            };
            await UntilAsync(async () =>
            {
                try
                {
                    return (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("connected").GetBoolean();
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, "Live web host connects cameras");
            Check(!(await http.GetFromJsonAsync<JsonElement>("/api/recording")).GetProperty("settings").GetProperty("enabled").GetBoolean(),
                "Live audio is tested with recording disabled");

            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("cameras")
                .EnumerateArray().All(camera => camera.GetProperty("status").GetString() == "idle"), "Headless previews are paused");
            var snapshot = await http.GetByteArrayAsync("/api/cameras/1/snapshot");
            Check(snapshot.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }), "A first headless snapshot temporarily starts JPEG capture");
            using (var preview = new ClientWebSocket())
            {
                await preview.ConnectAsync(new($"ws://127.0.0.1:{webPort}/api/video"), CancellationToken.None);
                await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("cameras")
                    .EnumerateArray().All(camera => camera.GetProperty("live").GetBoolean()), "A browser starts all shared previews");
                preview.Abort();
            }
            await UntilAsync(async () => (await http.GetFromJsonAsync<JsonElement>("/api/state")).GetProperty("cameras")
                .EnumerateArray().All(camera => camera.GetProperty("status").GetString() == "idle"), "Closing the browser pauses previews again");
            Check((await http.GetByteArrayAsync("/api/cameras/1/snapshot")).Length > 0,
                "Paused previews retain snapshot downloads");

            using (var first = await ConnectAsync(webPort, 1))
            {
                string? originalStream = null;
                var epoch = await ReadEpochAsync(first, expectAudio: true,
                    metadata => originalStream = metadata.GetProperty("streamId").GetString());
                Check((await http.GetFromJsonAsync<JsonElement>("/api/cameras/1/talkback")).GetProperty("status").GetString() != "waiting",
                    "Talkback capability checks recognize active live media while JPEG previews are paused");
                Check(!(await http.GetFromJsonAsync<JsonElement>("/api/recording")).GetProperty("settings").GetProperty("enabled").GetBoolean(),
                    "Receiving live audio does not enable recording");
                var path = Path.Combine(directory, "live.mp4");
                await File.WriteAllBytesAsync(path, epoch.SelectMany(bytes => bytes).ToArray());
                await VerifyMediaAsync(path);
                using var second = await ConnectAsync(webPort, 1);
                var lateEpoch = await ReadEpochAsync(second, expectAudio: true);
                using var compatible = await ConnectAsync(webPort, 1, compatibility: true);
                string? compatibleStream = null;
                var compatibleEpoch = await ReadEpochAsync(compatible, expectAudio: true,
                    metadata => compatibleStream = metadata.GetProperty("streamId").GetString());
                Check(compatibleStream != originalStream && FfmpegLiveMediaSource.Describe(compatibleEpoch[0]).H264Profile == 66,
                    "Compatibility listeners receive a separate baseline H.264 stream while original listeners keep their session");
                using var compatiblePeer = await ConnectAsync(webPort, 1, compatibility: true);
                await ReadEpochAsync(compatiblePeer, expectAudio: true,
                    metadata => Check(metadata.GetProperty("streamId").GetString() == compatibleStream,
                        "Compatible listeners on one camera share the same encoder epoch"));
                var compatiblePath = Path.Combine(directory, "compatible.mp4");
                await File.WriteAllBytesAsync(compatiblePath, compatibleEpoch.SelectMany(bytes => bytes).ToArray());
                await VerifyMediaAsync(compatiblePath);
                Check(epoch[0].SequenceEqual(lateEpoch[0]), "A late browser receives the shared live initialization and current keyframe");
            }

            using (var silent = await ConnectAsync(webPort, 2))
            {
                await ReadEpochAsync(silent, expectAudio: false);
                Check(true, "A camera without audio provides live video and explicit no-audio metadata");
            }

            await VerifyRewindAsync(http, directory);
            await VerifyAvailableRewindAsync(http, directory, settings, sleepPath);

            using (var changed = await ConnectAsync(webPort, 1))
            {
                await ReadEpochAsync(changed, expectAudio: true);
                using var changedCompatibility = await ConnectAsync(webPort, 1, compatibility: true);
                await ReadEpochAsync(changedCompatibility, expectAudio: true);
                (await http.PostAsync("/api/quality", null)).EnsureSuccessStatusCode();
                await ExpectClosedAsync(changed);
                await ExpectClosedAsync(changedCompatibility);
                Check(true, "Changing connection generation stops live sound before restarting camera playback");
            }

            settings = settings with
            {
                Sources = [settings.Sources[0] with
                {
                    MotionActivated = true,
                    PollIntervalSeconds = .1m
                }]
            };
            await File.WriteAllTextAsync(sleepPath, "");
            (await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(settings, null))).EnsureSuccessStatusCode();
            using (var battery = await ConnectAsync(webPort, 1))
            {
                await ReadStateAsync(battery, "waiting");
                File.Delete(sleepPath);
                await ReadEpochAsync(battery, expectAudio: true);
                await File.WriteAllTextAsync(sleepPath, "");
                await ReadStateAsync(battery, "waiting");
                File.Delete(sleepPath);
                await ReadEpochAsync(battery, expectAudio: true);
                Check(true, "Live audio waits through sleep, discards the old epoch, and resumes after wake");
            }

            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/cameras/1/live");
            foreign.Headers.Add("Origin", "http://other.example");
            Check((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden,
                "Live audio retains same-origin protections");

            (await http.PostAsJsonAsync("/api/integrations/setup", new
            {
            })).EnsureSuccessStatusCode();
            var response = await http.PostAsJsonAsync("/api/integrations/tokens", new TokenRequest("Live reader", ["read"], null));
            response.EnsureSuccessStatusCode();
            var reader = await response.Content.ReadFromJsonAsync<JsonElement>();
            using (var readClient = new HttpClient { BaseAddress = http.BaseAddress })
            {
                readClient.DefaultRequestHeaders.Authorization = new("Bearer", reader.GetProperty("token").GetString());
                Check((await readClient.GetAsync("/api/playback")).IsSuccessStatusCode &&
                    (await readClient.PutAsJsonAsync("/api/playback", new LiveRewindSettings(true))).StatusCode == HttpStatusCode.Forbidden,
                    "Read access can inspect playback but cannot change server capture settings");
            }
            using (var authorized = await ConnectAsync(webPort, 1, reader.GetProperty("token").GetString(), compatibility: true))
            {
                await ReadEpochAsync(authorized, expectAudio: true);
                await File.WriteAllTextAsync(sleepPath, "");
                await ReadStateAsync(authorized, "waiting");
                var tokenId = reader.GetProperty("details").GetProperty("id").GetString();
                (await http.DeleteAsync("/api/integrations/tokens/" + tokenId)).EnsureSuccessStatusCode();
                await ExpectClosedAsync(authorized);
                Check(true, "Revoking read access closes a live listener even while its camera is sleeping");
            }

            using var unauthorized = new HttpClient { BaseAddress = http.BaseAddress };
            Check((await unauthorized.GetAsync("/api/cameras/1/live")).StatusCode == HttpStatusCode.Unauthorized,
                "Live audio requires read access when authentication is enabled");
            Check((await unauthorized.GetAsync("/api/playback")).StatusCode == HttpStatusCode.Unauthorized &&
                (await unauthorized.GetAsync("/api/playback/1/missing/init")).StatusCode == HttpStatusCode.Unauthorized &&
                (await unauthorized.GetAsync("/api/playback/1/missing/window?at=0")).StatusCode == HttpStatusCode.Unauthorized,
                "Rewind metadata and media require read authorization");
        }
        finally
        {
            await StopAsync(server);
            await StopAsync(fixture);
        }
    }

    private static async Task VerifyRewindAsync(HttpClient http, string directory)
    {
        (await http.PutAsJsonAsync("/api/playback", new LiveRewindSettings(true, 900, 256))).EnsureSuccessStatusCode();
        await UntilAsync(async () =>
        {
            var state = await http.GetFromJsonAsync<JsonElement>("/api/playback");
            return state.GetProperty("cameras").EnumerateArray().Count(camera => camera.GetProperty("ranges").GetArrayLength() > 0) == 2 &&
                state.GetProperty("cameras").EnumerateArray().All(camera =>
                    camera.GetProperty("ranges")[0].GetProperty("end").GetInt64() -
                    camera.GetProperty("ranges")[0].GetProperty("start").GetInt64() > 4000);
        }, "Server rewind buffers multiple cameras before any playback browser subscribes");
        var snapshot = await http.GetFromJsonAsync<JsonElement>("/api/playback");
        Check(snapshot.GetProperty("settings").GetProperty("seconds").GetInt32() == 900 &&
            !(await http.GetFromJsonAsync<JsonElement>("/api/recording")).GetProperty("settings").GetProperty("enabled").GetBoolean(),
            "Rewind accepts longer user-selected history and never enables permanent recording");
        var recordingState = await http.GetFromJsonAsync<JsonElement>("/api/recording");
        var recordingSettings = recordingState.GetProperty("settings").Deserialize<RecordingSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        (await http.PostAsJsonAsync("/api/recording", recordingSettings with
        {
            Directory = Path.Combine(directory, "saved-replays"),
            Enabled = false
        })).EnsureSuccessStatusCode();
        var commonStart = snapshot.GetProperty("cameras").EnumerateArray()
            .Max(camera => camera.GetProperty("ranges")[0].GetProperty("start").GetInt64()) + 500;
        var commonEnd = commonStart + 2000;
        var generation = snapshot.GetProperty("generation").GetInt32();
        var save = await http.PostAsJsonAsync("/api/playback/recordings",
            new SaveRewindRecordingRequest(generation, commonEnd, commonStart, [1, 2]));
        save.EnsureSuccessStatusCode();
        var saved = await save.Content.ReadFromJsonAsync<RecordingEvent[]>();
        Check(saved!.Length == 2 && saved.All(item => item.Status == "ready" && item.Trigger.Source == "replay" &&
            item.StartedAt.ToUnixTimeMilliseconds() == commonStart && item.DurationSeconds == 2),
            "HTTP backwards grid selection saves both cameras with searchable replay metadata");
        var found = await http.PostAsJsonAsync("/api/recordings/search", new RecordingSearch { SavedReplayOnly = true });
        found.EnsureSuccessStatusCode();
        Check((await found.Content.ReadFromJsonAsync<RecordingEvent[]>())!.Length == 2,
            "View recordings search returns saved replays immediately");
        foreach (var item in saved)
        {
            var clipPath = Path.Combine(directory, item.Id + ".mp4");
            await File.WriteAllBytesAsync(clipPath, await http.GetByteArrayAsync("/api/recordings/" + item.Id + "/video"));
            if (item.Channel == 1)
            {
                await VerifyMediaAsync(clipPath);
            }
            else
            {
                await new FfmpegProcess("ffmpeg").FileAsync(["-xerror", "-i", clipPath, "-map", "0:v:0", "-f", "null", "-"], CancellationToken.None);
                Check(true, "Saved silent-camera footage decodes without an audio track");
            }
        }
        var focused = await http.PostAsJsonAsync("/api/playback/recordings",
            new SaveRewindRecordingRequest(generation, commonStart, commonEnd, [2]));
        focused.EnsureSuccessStatusCode();
        Check((await focused.Content.ReadFromJsonAsync<RecordingEvent[]>())!.Single().Channel == 2,
            "HTTP focused save includes only the requested camera");
        Check((await http.PostAsJsonAsync("/api/playback/recordings",
            new SaveRewindRecordingRequest(generation, commonStart, commonStart, [1]))).StatusCode == HttpStatusCode.BadRequest &&
            (await http.PostAsJsonAsync("/api/playback/recordings",
            new SaveRewindRecordingRequest(generation + 1, commonStart, commonEnd, [1]))).StatusCode == HttpStatusCode.Conflict,
            "HTTP rejects zero-length selections and stale connection generations");
        using (var foreignSave = new HttpRequestMessage(HttpMethod.Post, "/api/playback/recordings"))
        {
            foreignSave.Headers.Add("Origin", "http://other.example");
            foreignSave.Content = JsonContent.Create(new SaveRewindRecordingRequest(generation, commonStart, commonEnd, [1]));
            Check((await http.SendAsync(foreignSave)).StatusCode == HttpStatusCode.Forbidden,
                "Replay saves enforce the existing same-origin policy");
        }
        var range = snapshot.GetProperty("cameras").EnumerateArray().Single(camera => camera.GetProperty("channel").GetInt32() == 1).GetProperty("ranges")[0];
        var prefix = $"/api/playback/1/{range.GetProperty("epoch").GetString()}";
        var at = range.GetProperty("start").GetInt64();
        var playlist = await http.GetFromJsonAsync<JsonElement>($"{prefix}?at={at}");
        using var media = new MemoryStream();
        media.Write(await http.GetByteArrayAsync(prefix + "/init"));
        var fragments = new List<VideoFragment>();
        foreach (var fragment in playlist.GetProperty("fragments").EnumerateArray())
        {
            media.Write(await http.GetByteArrayAsync(prefix + "/fragments/" + fragment.GetProperty("sequence").GetInt64()));
        }
        media.Position = 0;
        await FragmentedMp4.ReadAsync(media, _ => Task.CompletedTask, fragment => { fragments.Add(fragment); return Task.CompletedTask; }, CancellationToken.None);
        Check(fragments.Count >= 4 && fragments.Zip(fragments.Skip(1)).All(pair => pair.Second.StartSeconds > pair.First.StartSeconds),
            "HTTP rewind serves playable keyframe fragments with increasing decode timestamps");
        var path = Path.Combine(directory, "rewind.mp4");
        await File.WriteAllBytesAsync(path, media.ToArray());
        await VerifyMediaAsync(path);
        var capturedWindow = await http.GetByteArrayAsync(prefix + "/window?at=" + at);
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(capturedWindow));
        var header = JsonSerializer.Deserialize<RewindWindowHeader>(capturedWindow.AsSpan(4, headerLength),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Check(header.Playlist.Epoch == range.GetProperty("epoch").GetString() && header.Playlist.HasAudio &&
            header.Playlist.Fragments.Length == header.FragmentLengths.Length && header.InitializationLength > 0 &&
            4L + headerLength + header.InitializationLength + header.FragmentLengths.Sum(length => (long)length) == capturedWindow.Length,
            "HTTP complete-window response pairs its timing and media lengths in one admission");
        var windowPath = Path.Combine(directory, "rewind-window.mp4");
        await File.WriteAllBytesAsync(windowPath, capturedWindow.AsMemory(4 + headerLength).ToArray());
        await VerifyMediaAsync(windowPath);
        Check((await http.GetAsync(prefix + "?at=" + (at - 10000))).StatusCode == HttpStatusCode.Gone &&
            (await http.GetAsync(prefix + "/fragments/999999")).StatusCode == HttpStatusCode.Gone &&
            (await http.GetAsync("/api/playback/999/missing/init")).StatusCode == HttpStatusCode.Gone,
            "Unavailable historical media returns Gone instead of live or another camera's footage");
        Check((await http.GetAsync(prefix + "/window?at=" + (at - 10000))).StatusCode == HttpStatusCode.Gone &&
            (await http.GetAsync("/api/playback/999/missing/window?at=0")).StatusCode == HttpStatusCode.Gone &&
            (await http.GetAsync(prefix + "/window?at=" + (DateTimeOffset.MinValue.ToUnixTimeMilliseconds() - 1))).StatusCode == HttpStatusCode.BadRequest,
            "Complete-window reads preserve true expiry, channel identity and timestamp validation");
        Check((await http.PutAsJsonAsync("/api/playback", new LiveRewindSettings(true, 0))).StatusCode == HttpStatusCode.BadRequest,
            "Invalid rewind settings are rejected through HTTP");
        using var foreign = new HttpRequestMessage(HttpMethod.Get, prefix + "/init");
        foreign.Headers.Add("Origin", "http://other.example");
        Check((await http.SendAsync(foreign)).StatusCode == HttpStatusCode.Forbidden, "Rewind media enforces same-origin protection");
        using var foreignWindow = new HttpRequestMessage(HttpMethod.Get, prefix + "/window?at=" + at);
        foreignWindow.Headers.Add("Origin", "http://other.example");
        Check((await http.SendAsync(foreignWindow)).StatusCode == HttpStatusCode.Forbidden, "Complete-window media enforces the same read-origin protection");
        (await http.PutAsJsonAsync("/api/playback", new LiveRewindSettings(false, 900, 256))).EnsureSuccessStatusCode();
        Check((await http.GetAsync(prefix + "/init")).StatusCode == HttpStatusCode.Gone &&
            (await http.GetFromJsonAsync<JsonElement>("/api/playback")).GetProperty("cameras").GetArrayLength() == 0,
            "Disabling rewind immediately removes historical media endpoints");
    }

    private static async Task VerifyAvailableRewindAsync(HttpClient http, string directory, ViewerSettings settings, string sleepPath)
    {
        var partialSettings = settings with
        {
            Sources = [settings.Sources[0] with
            {
                MotionActivated = true,
                PollIntervalSeconds = .1m
            }, settings.Sources[1] with
            {
                MainStream = $"rtsp://127.0.0.1:{Port()}/unavailable",
                MotionActivated = true,
                PollIntervalSeconds = .1m
            }]
        };
        (await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(partialSettings, null))).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync("/api/playback", new LiveRewindSettings(true, 900, 256))).EnsureSuccessStatusCode();
        await UntilAsync(async () =>
        {
            var state = await http.GetFromJsonAsync<JsonElement>("/api/playback");
            var cameras = state.GetProperty("cameras").EnumerateArray().ToArray();
            var ranges = cameras.Single(camera => camera.GetProperty("channel").GetInt32() == 1).GetProperty("ranges");
            return ranges.GetArrayLength() > 0 && ranges[0].GetProperty("end").GetInt64() - ranges[0].GetProperty("start").GetInt64() > 2000 &&
                cameras.Single(camera => camera.GetProperty("channel").GetInt32() == 2).GetProperty("ranges").GetArrayLength() == 0;
        }, "One motion camera buffers footage while another selected camera has none");
        var snapshot = await http.GetFromJsonAsync<JsonElement>("/api/playback");
        var generation = snapshot.GetProperty("generation").GetInt32();
        var firstRange = snapshot.GetProperty("cameras").EnumerateArray().Single(camera => camera.GetProperty("channel").GetInt32() == 1)
            .GetProperty("ranges")[0];
        var start = firstRange.GetProperty("start").GetInt64() - 1000;
        var end = firstRange.GetProperty("end").GetInt64();

        var response = await http.PostAsJsonAsync("/api/playback/recordings", new SaveRewindRecordingRequest(generation, end, start, [2, 1]));
        response.EnsureSuccessStatusCode();
        var saved = (await response.Content.ReadFromJsonAsync<RecordingEvent[]>())!.Single();
        Check(saved.Channel == 1 && saved.StartedAt.ToUnixTimeMilliseconds() == start + 1000 &&
            saved.EndedAt!.Value.ToUnixTimeMilliseconds() == end && saved.Status == "ready",
            "HTTP grid saves crop unavailable beginnings and skip empty cameras, including backwards selections");
        Check((await http.PostAsJsonAsync("/api/playback/recordings", new SaveRewindRecordingRequest(generation, start, end, [2]))).StatusCode == HttpStatusCode.Conflict &&
            (await http.PostAsJsonAsync("/api/playback/recordings", new SaveRewindRecordingRequest(generation, start - 1000, start, [1, 2]))).StatusCode == HttpStatusCode.Conflict,
            "HTTP missing-footage errors apply only when no selected camera has overlapping footage");

        await File.WriteAllTextAsync(sleepPath, "");
        await UntilAsync(async () =>
        {
            var state = await http.GetFromJsonAsync<JsonElement>("/api/playback");
            return state.GetProperty("cameras").EnumerateArray().Single(camera => camera.GetProperty("channel").GetInt32() == 1)
                .GetProperty("status").GetString() == "waiting";
        }, "The motion camera returns to sleep with its earlier footage retained");
        // Allow a visible wall-clock gap between capture epochs.
        await Task.Delay(1000);
        File.Delete(sleepPath);
        await UntilAsync(async () =>
        {
            var state = await http.GetFromJsonAsync<JsonElement>("/api/playback");
            return state.GetProperty("cameras").EnumerateArray().Single(camera => camera.GetProperty("channel").GetInt32() == 1)
                .GetProperty("ranges").GetArrayLength() >= 2;
        }, "The motion camera wakes and buffers a second burst");
        snapshot = await http.GetFromJsonAsync<JsonElement>("/api/playback");
        var ranges = snapshot.GetProperty("cameras").EnumerateArray().Single(camera => camera.GetProperty("channel").GetInt32() == 1)
            .GetProperty("ranges").EnumerateArray().ToArray();
        end = ranges.Last().GetProperty("end").GetInt64();
        response = await http.PostAsJsonAsync("/api/playback/recordings", new SaveRewindRecordingRequest(generation, start, end, [1, 2]));
        response.EnsureSuccessStatusCode();
        var bursts = (await response.Content.ReadFromJsonAsync<RecordingEvent[]>())!.OrderBy(item => item.StartedAt).ToArray();
        Check(bursts.Length == ranges.Length && bursts.All(item => item.Channel == 1 && item.Status == "ready") &&
            bursts.Zip(bursts.Skip(1)).All(pair => pair.First.EndedAt < pair.Second.StartedAt) &&
            bursts.Sum(item => item.DurationSeconds) < (end - (start + 1000)) / 1000.0,
            "HTTP saves waking-camera bursts as separate recordings without the sleeping gap");
        foreach (var burst in bursts)
        {
            var path = Path.Combine(directory, "burst-" + burst.Id + ".mp4");
            await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync("/api/recordings/" + burst.Id + "/video"));
            await VerifyMediaAsync(path);
        }
        var search = await http.PostAsJsonAsync("/api/recordings/search", new RecordingSearch { SavedReplayOnly = true });
        search.EnsureSuccessStatusCode();
        var found = (await search.Content.ReadFromJsonAsync<RecordingEvent[]>())!;
        Check(bursts.All(burst => found.Any(item => item.Id == burst.Id)),
            "View recordings immediately finds every saved burst from a partial grid selection");
        (await http.PutAsJsonAsync("/api/playback", new LiveRewindSettings(false, 900, 256))).EnsureSuccessStatusCode();
        (await http.PostAsJsonAsync("/api/connection", new ConnectionRequest(settings, null))).EnsureSuccessStatusCode();
    }

    private static async Task SharedSessionTestsAsync()
    {
        var source = new TestSource();
        var hub = new LiveMediaHub(source);
        var settings = new ViewerSettings();
        var first = hub.Subscribe(settings, "", 1, null);
        var second = hub.Subscribe(settings, "", 1, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var drain = Task.Run(async () =>
        {
            await foreach (var packet in second.ReadAllAsync(deadline.Token))
            {
            }
        });
        await source.Started.Task.WaitAsync(deadline.Token);
        Check(source.Starts == 1 && hub.SessionCount == 1, "Multiple live listeners share one encoder");
        await first.DisposeAsync();
        Check(source.Stops == 0, "Closing one browser keeps another browser listening");
        var slow = hub.Subscribe(settings, "", 1, null);
        await Task.Delay(1400, deadline.Token);
        var packets = new List<LiveMediaPacket>();
        await foreach (var packet in slow.ReadAllAsync(deadline.Token))
        {
            packets.Add(packet);
        }

        Check(packets.Count == 1 && packets[0].Status == "retrying",
            "A slow listener discards stale media and closes without stalling other browsers");
        await slow.DisposeAsync();
        await second.DisposeAsync();
        await drain;
        Check(source.Stops == 1 && hub.SessionCount == 0, "The final listener releases its encoder and bounded queues");
        var reconnect = hub.Subscribe(settings, "", 1, null);
        await UntilAsync(() => Task.FromResult(source.Starts == 2), "Listening again starts a fresh media epoch");
        await hub.StopAsync();
        await reconnect.DisposeAsync();
        Check(hub.SessionCount == 0 && source.Stops == 2, "Viewer shutdown awaits every live encoder");

        var largeSource = new TestSource(1024 * 1024);
        var largeHub = new LiveMediaHub(largeSource);
        await using var largeListener = largeHub.Subscribe(settings, "", 1, null);
        await largeSource.Started.Task.WaitAsync(deadline.Token);
        await Task.Delay(1000, deadline.Token);
        var largePackets = new List<LiveMediaPacket>();
        await foreach (var packet in largeListener.ReadAllAsync(deadline.Token))
        {
            largePackets.Add(packet);
        }

        Check(largePackets.Count == 1 && largePackets[0].Status == "retrying" && largePackets[0].Bytes is null,
            "The byte limit closes a listener before its message-count limit and discards retained media");
        await largeListener.DisposeAsync();
        Check(largeSource.Stops == 1 && largeHub.SessionCount == 0, "An overflowing listener releases its encoder");

        var asleep = new TestSource();
        var availability = new BatteryCameraAvailability(_ => Task.FromResult(new ProbeResult(false, 404, "Sleeping")), TimeSpan.FromMilliseconds(10));
        var idleHub = new LiveMediaHub(asleep);
        await using var waiting = idleHub.Subscribe(settings, "", 1, availability);
        await Task.Delay(150);
        Check(asleep.Starts == 0, "Waiting for a sleeping camera launches no live media encoder");
        await idleHub.StopAsync();
    }

    private static async Task<List<byte[]>> ReadEpochAsync(ClientWebSocket socket, bool expectAudio,
        Action<JsonElement>? initialization = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var chunks = new List<byte[]>();
        var initialized = false;
        while (chunks.Count < 4)
        {
            var (type, bytes) = await ReadAsync(socket, deadline.Token);
            if (type == WebSocketMessageType.Text)
            {
                using var document = JsonDocument.Parse(bytes);
                var metadata = document.RootElement;
                if (metadata.GetProperty("type").GetString() == "init")
                {
                    initialization?.Invoke(metadata);
                    Check(metadata.GetProperty("hasAudio").GetBoolean() == expectAudio,
                        "Live metadata accurately reports available audio");
                    Check(metadata.GetProperty("mimeType").GetString()!.Contains("mp4a") == expectAudio,
                        "Browser MIME metadata matches actual live tracks");
                    initialized = true;
                    chunks.Clear();
                }
            }
            else if (type == WebSocketMessageType.Binary && initialized)
            {
                chunks.Add(bytes);
            }
            else if (type == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Live socket closed before receiving media.");
            }
        }

        var description = FfmpegLiveMediaSource.Describe(chunks[0]);
        var expectedCodec = Environment.GetEnvironmentVariable("RTSP_FIXTURE_CODEC") == "hevc" ? "hev1." : "avc1.";
        Check(description.HasAudio == expectAudio && description.MimeType.Contains(expectedCodec),
            "Live initialization preserves the RTSP video codec and optional AAC audio");
        return chunks;
    }

    private static async Task ReadStateAsync(ClientWebSocket socket, string status)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var (type, bytes) = await ReadAsync(socket, deadline.Token);
            if (type != WebSocketMessageType.Text)
            {
                continue;
            }

            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.TryGetProperty("status", out var value) && value.GetString() == status)
            {
                return;
            }
        }
    }

    private static async Task ExpectClosedAsync(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while ((await ReadAsync(socket, deadline.Token)).Type != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReadAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            await message.WriteAsync(buffer.AsMemory(0, result.Count), token);
            if (message.Length > LiveMediaSubscription.MaximumQueuedBytes)
            {
                throw new InvalidDataException("Live socket message exceeded its bound.");
            }
        }
        while (!result.EndOfMessage);
        return (result.MessageType, message.ToArray());
    }

    private static async Task<ClientWebSocket> ConnectAsync(int port, int channel, string? token = null, bool compatibility = false)
    {
        var socket = new ClientWebSocket();
        if (token is not null)
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new($"ws://127.0.0.1:{port}/api/cameras/{channel}/live" +
            (compatibility ? "?compatibility=true" : "")), deadline.Token);
        return socket;
    }

    private static async Task VerifyMediaAsync(string path)
    {
        var ffmpeg = new FfmpegProcess("ffmpeg");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ffmpeg.FileAsync(["-xerror", "-i", path, "-f", "null", "-"], deadline.Token);
        var audible = false;
        await ffmpeg.RunAsync(["-i", path, "-map", "0:a:0", "-f", "s16le", "pipe:1"], null,
            async (stream, token) =>
            {
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                {
                    audible |= buffer.AsSpan(0, count).ContainsAnyExcept((byte)0);
                }
            }, deadline.Token);
        Check(audible, "Actual live RTSP fragments decode video and non-silent AAC audio");
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process Start(string executable, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            await Task.Delay(100, deadline.Token);
        }

        Check(true, message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }

        Console.WriteLine("PASS: " + message);
    }

    private sealed class TestSource(int fragmentSize = 1) : ILiveMediaSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts;
        public int Stops;

        public async Task RunAsync(ViewerSettings settings, string password, int channel,
            Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            Interlocked.Increment(ref Starts);
            Started.TrySetResult();
            try
            {
                await initialized(new([1], "video/mp4", true));
                while (true)
                {
                    await fragment(new(new byte[fragmentSize], .1));
                    await Task.Delay(100, token);
                }
            }
            finally
            {
                Interlocked.Increment(ref Stops);
            }
        }
    }
}
