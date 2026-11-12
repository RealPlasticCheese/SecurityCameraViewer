using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class CameraEnhancementTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "camera-enhancement-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var options = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["settings"] = path }).Build();
        using var document = new JsonAppSettingsStore(options);
        var configurations = new WebSettingsStore(document);
        var store = new CameraEnhancementStore(document);
        var legacyId = 1.ToString("x32");
        Check(store.List("default")[legacyId] == new CameraEnhancementSettings(), "Existing settings default every slider to Balanced and enhancements off");
        await store.UpdateAsync("default", legacyId, new() { Brightness = 18, Enabled = true });
        await configurations.SaveConfigurationAsync(new ViewerSettings { AutoConnect = false }.WithSources(), "camera-secret");
        Check(store.List("default")[legacyId].Brightness == 18, "Expanding legacy camera sources preserves their enhancement identity");
        var first = document.Read().Configurations[0].Connection.Sources[0];
        var second = document.Read().Configurations[0].Connection.Sources[1];
        var custom = new CameraEnhancementUpdate
        {
            Enabled = true, Brightness = 18, Contrast = -20, Gamma = 1.35, Denoise = 60,
            Sharpness = 88, Radius = 2.4, Saturation = -10, Vibrance = 15
        };
        var expected = custom.Apply(new());
        await store.UpdateAsync("default", first.Id, custom);
        await store.UpdateAsync("default", first.Id, new() { Enabled = false });
        Check(store.List("default")[first.Id] == expected with { Enabled = false }, "Switching off preserves all eight saved sliders");
        await store.UpdateAsync("default", first.Id, new() { Enabled = true });
        using (var restored = new JsonAppSettingsStore(options))
        {
            var otherBrowser = new CameraEnhancementStore(restored);
            Check(otherBrowser.List("default")[first.Id] == expected, "New server and browser reads restore every slider and enabled preference");
            Check(restored.Read().Configurations[0].Password == "camera-secret", "Enhancement updates preserve camera credentials");
        }
        var snapshot = document.Read();
        snapshot.Configurations[0].Enhancements.Clear();
        Check(store.List("default")[first.Id] == expected, "Returned enhancement snapshots cannot mutate shared settings");
        var otherId = Guid.NewGuid().ToString("N");
        var connection = document.Read().Configurations[0].Connection;
        await configurations.SaveConfigurationAsync(connection with { ConfigurationId = otherId, ConfigurationName = "Other cameras" }, "other-secret");
        await store.UpdateAsync(otherId, first.Id, new() { Gamma = 1.8 });
        Check(store.List("default")[first.Id] == expected && store.List(otherId)[first.Id].Gamma == 1.8,
            "The same camera ID in different configurations keeps independent settings");
        await Task.WhenAll(
            Task.Run(() => store.UpdateAsync("default", first.Id, new() { Brightness = 21 })),
            Task.Run(() => store.UpdateAsync("default", first.Id, new() { Gamma = 1.6 })),
            Task.Run(() => store.UpdateAsync("default", first.Id, new() { Enabled = false })),
            Task.Run(() => store.UpdateAsync("default", second.Id, new() { Contrast = 32 })));
        Check(store.List("default")[first.Id] == expected with { Brightness = 21, Gamma = 1.6, Enabled = false } &&
            store.List("default")[second.Id].Contrast == 32, "Concurrent partial edits preserve separate fields and cameras");
        connection = connection with { Sources = [.. connection.Sources.Reverse().Select(camera => camera with { Name = "Renamed " + camera.Channel })] };
        await configurations.SaveConfigurationAsync(connection, "camera-secret");
        Check(store.List("default")[first.Id].Brightness == 21 && store.List("default")[second.Id].Contrast == 32,
            "Camera reorder and rename retain enhancement settings by stable identity");

        var ranges = new (Func<double, CameraEnhancementUpdate> Update, double Minimum, double Maximum)[]
        {
            (value => new() { Brightness = value }, -50, 50),
            (value => new() { Contrast = value }, -50, 50),
            (value => new() { Gamma = value }, 0.5, 2),
            (value => new() { Denoise = value }, 0, 100),
            (value => new() { Sharpness = value }, 0, 100),
            (value => new() { Radius = value }, 0.5, 4),
            (value => new() { Saturation = value }, -100, 50),
            (value => new() { Vibrance = value }, -50, 50)
        };
        foreach (var (update, minimum, maximum) in ranges)
        {
            await store.UpdateAsync("default", first.Id, update(minimum));
            await store.UpdateAsync("default", first.Id, update(maximum));
            var before = File.ReadAllBytes(path);
            foreach (var invalid in new[] { minimum - 0.01, maximum + 0.01, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                await RejectAsync<ArgumentException>(() => store.UpdateAsync("default", first.Id, update(invalid)));
            }
            Check(File.ReadAllBytes(path).SequenceEqual(before), "Supported slider endpoints are accepted; invalid and non-finite values never change saved data");
        }
        var original = File.ReadAllBytes(path);
        await RejectAsync<ArgumentException>(() => store.UpdateAsync("missing", first.Id, new()));
        await RejectAsync<ArgumentException>(() => store.UpdateAsync("default", "missing", new()));
        await RejectAsync<ArgumentNullException>(() => store.UpdateAsync("default", first.Id, null!));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await RejectAsync<OperationCanceledException>(() => store.UpdateAsync("default", first.Id, new() { Enabled = true }, cancellation.Token));
        }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Unknown cameras/configurations, missing requests and cancelled writes preserve saved data");
        await RejectAsync<ArgumentException>(() => document.UpdateAsync(settings => settings with
        {
            Configurations = settings.Configurations.Select(configuration => configuration.Connection.ConfigurationId == "default"
                ? configuration with { Enhancements = new() { ["missing"] = new() } } : configuration).ToArray()
        }));
        await RejectAsync<ArgumentException>(() => document.UpdateAsync(settings => settings with
        {
            Configurations = settings.Configurations.Select(configuration => configuration.Connection.ConfigurationId == "default"
                ? configuration with { Enhancements = null! } : configuration).ToArray()
        }));
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Malformed enhancement sections cannot replace the settings document");
        var saved = store.List("default")[first.Id];
        File.Delete(path);
        Directory.CreateDirectory(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                await RejectAsync<UnauthorizedAccessException>(() => store.UpdateAsync("default", first.Id, new() { Enabled = true }));
            }
            else
            {
                await RejectAsync<IOException>(() => store.UpdateAsync("default", first.Id, new() { Enabled = true }));
            }
            Check(store.List("default")[first.Id] == saved, "A disk write failure leaves the last saved enhancement state intact");
        }
        finally
        {
            Directory.Delete(path);
            PrivateFile.WriteBytes(path, original);
        }
        await configurations.SaveConfigurationAsync(connection with { Sources = connection.Sources.Where(camera => camera.Id != second.Id).ToArray() }, "camera-secret");
        Check(!document.Read().Configurations.Single(configuration => configuration.Connection.ConfigurationId == "default").Enhancements.ContainsKey(second.Id),
            "Deleting a camera removes only its saved enhancements");
        await HttpAsync(directory, configurations, document, options, first.Id);
    }

    private static async Task HttpAsync(string directory, WebSettingsStore configurations, IAppSettingsStore document,
        IConfiguration options, string cameraId)
    {
        var integrations = new IntegrationSettings(document, options);
        var (_, adminSecret) = integrations.CreateToken("Enhancement administrator", ["admin"], null);
        var (_, readerSecret) = integrations.CreateToken("Enhancement reader", ["read"], null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var firstBrowser = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        using var secondBrowser = new HttpClient { BaseAddress = firstBrowser.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        firstBrowser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
        secondBrowser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(ViewerService).Assembly.Location, "--urls", firstBrowser.BaseAddress.ToString(), "--settings", configurations.FilePath })
        {
            info.ArgumentList.Add(argument);
        }
        var endpoint = $"/api/configurations/default/cameras/{cameraId}/enhancements";
        for (var run = 0; run < 2; run++)
        {
            using var server = Process.Start(info)!;
            var stdout = server.StandardOutput.ReadToEndAsync();
            var stderr = server.StandardError.ReadToEndAsync();
            try
            {
                await UntilAsync(firstBrowser);
                if (run == 0)
                {
                    using var update = await firstBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Brightness = 17, Enabled = true });
                    Check(update.IsSuccessStatusCode, "The browser API saves enhancement edits");
                }
                using var state = await secondBrowser.GetFromJsonAsync<JsonDocument>("/api/state");
                var enhancements = state!.RootElement.GetProperty("enhancements").GetProperty(cameraId);
                Check(enhancements.GetProperty("brightness").GetDouble() == 17 && enhancements.GetProperty("enabled").GetBoolean(),
                    "Another browser and a restarted server see saved enhancement values");
                if (run != 0)
                {
                    continue;
                }
                using var disable = await secondBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Enabled = false });
                var disabled = await disable.Content.ReadFromJsonAsync<CameraEnhancementSettings>();
                Check(disable.IsSuccessStatusCode && disabled!.Brightness == 17 && !disabled.Enabled, "API switch updates do not erase sliders");
                using var enable = await firstBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Enabled = true });
                using var bad = await firstBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Gamma = 0 });
                using var missing = await firstBrowser.PatchAsJsonAsync(endpoint.Replace(cameraId, "missing"), new CameraEnhancementUpdate());
                using var malformed = await firstBrowser.PatchAsync(endpoint, new StringContent("{invalid", System.Text.Encoding.UTF8, "application/json"));
                using var absent = await firstBrowser.PatchAsync(endpoint, new StringContent("null", System.Text.Encoding.UTF8, "application/json"));
                Check(bad.StatusCode == HttpStatusCode.BadRequest && missing.StatusCode == HttpStatusCode.BadRequest &&
                    malformed.StatusCode == HttpStatusCode.BadRequest && absent.StatusCode == HttpStatusCode.BadRequest,
                    "Invalid slider values, unknown cameras and malformed/missing bodies return HTTP 400");
                secondBrowser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerSecret);
                using var denied = await secondBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Brightness = 40 });
                secondBrowser.DefaultRequestHeaders.Authorization = null;
                using var anonymous = await secondBrowser.PatchAsJsonAsync(endpoint, new CameraEnhancementUpdate { Brightness = 40 });
                Check(denied.StatusCode == HttpStatusCode.Forbidden && anonymous.StatusCode == HttpStatusCode.Unauthorized,
                    "Enhancement writes require existing administrator authorization");
                secondBrowser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);
                using var foreign = new HttpRequestMessage(HttpMethod.Patch, endpoint)
                {
                    Content = JsonContent.Create(new CameraEnhancementUpdate { Brightness = 40 })
                };
                foreign.Headers.Add("Origin", "https://unrelated.example");
                using var rejected = await firstBrowser.SendAsync(foreign);
                Check(rejected.StatusCode == HttpStatusCode.Forbidden, "Cross-origin enhancement writes use the existing request protection");
            }
            finally
            {
                if (!server.HasExited)
                {
                    server.Kill(true);
                }
                await server.WaitForExitAsync();
                await File.WriteAllTextAsync(Path.Combine(directory, $"server-{run}.log"), await stdout + await stderr);
            }
        }
    }

    private static async Task UntilAsync(HttpClient http)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var response = await http.GetAsync("/api/state");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new Exception("Enhancement test server did not become ready.");
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + description);
        }
        Console.WriteLine("PASS: " + description);
    }
}
