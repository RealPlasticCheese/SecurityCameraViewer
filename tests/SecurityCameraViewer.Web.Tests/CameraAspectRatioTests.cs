using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class CameraAspectRatioTests
{
    public static async Task RunAsync()
    {
        var source = new CameraSource { Channel = 1, MainStream = "rtsp://127.0.0.1/main" };
        var legacy = JsonSerializer.Deserialize<CameraSource>("{\"Channel\":1,\"MainStream\":\"rtsp://127.0.0.1/main\"}")!;
        Check(legacy.MainStreamAspectRatio == "auto" && legacy.SubStreamAspectRatio == "auto" && legacy.Validate() is null,
            "Existing camera configurations default both aspect ratios to Auto");
        foreach (var main in new[] { "auto", "4:3", "16:9", "1:1", "9:16", "32:9", "1.85:1", ".5:1", "1000000:1" })
        {
            foreach (var sub in new[] { "auto", "4:3", "16:9", "1:1", "9:16", "32:9", "1.85:1", ".5:1", "1000000:1" })
            {
                var camera = source with
                {
                    MainStreamAspectRatio = main,
                    SubStreamAspectRatio = sub
                };
                Check(camera.Validate() is null, "Independent main/substream ratios validate even without a substream URL");
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var restored = JsonSerializer.Deserialize<CameraSource>(JsonSerializer.Serialize(camera, options), options)!;
                Check(restored.MainStreamAspectRatio == main && restored.SubStreamAspectRatio == sub,
                    "Both aspect ratios round trip through the browser configuration format");
            }
        }
        foreach (var invalid in new string?[] { null, "", "AUTO", "16/9", "custom", "0:1", "1:0", "-1:1", "+1:1", "1:2:3", "1e3:1", "NaN:1", "Infinity:1", " 1:1", "1,5:1", new string('9', 310) + ":1" })
        {
            Check((source with { MainStreamAspectRatio = invalid! }).Validate() is not null,
                "Invalid main-stream ratios cannot be persisted");
            Check((source with { SubStreamAspectRatio = invalid! }).Validate() is not null,
                "Invalid substream ratios cannot be persisted");
        }

        var directory = Path.GetFullPath(Path.Combine("artifacts", "aspect-ratio-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "settings.json")
        }).Build();
        var settings = new ViewerSettings
        {
            AutoConnect = false,
            Sources = [source with { MainStreamAspectRatio = "4:3", SubStreamAspectRatio = "1.85:1", SubStream = "rtsp://127.0.0.1/sub" }]
        };
        using (var store = new JsonAppSettingsStore(configuration))
        {
            await new WebSettingsStore(store).SaveConfigurationAsync(settings, "");
            try
            {
                await new WebSettingsStore(store).SaveConfigurationAsync(settings with
                {
                    Sources = [source with { SubStreamAspectRatio = "invalid" }]
                }, "");
                throw new Exception("An invalid aspect ratio was saved.");
            }
            catch (ArgumentException)
            {
                Check(true, "Invalid ratio updates are rejected by the settings store");
            }
        }
        using (var restarted = new JsonAppSettingsStore(configuration))
        {
            var camera = new WebSettingsStore(restarted).LoadCatalog().Configurations.Single().Sources.Single();
            Check(camera.MainStreamAspectRatio == "4:3" && camera.SubStreamAspectRatio == "1.85:1",
                "Independent stream ratios survive server restart and rejected updates");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
        Console.WriteLine("PASS: " + message);
    }
}
