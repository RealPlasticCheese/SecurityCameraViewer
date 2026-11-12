using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class VideoTimeoutTests
{
    public static async Task RunAsync()
    {
        var legacy = JsonSerializer.Deserialize<ViewerSettings>("{}")!;
        Check(legacy.VideoTimeoutSeconds == 0 && legacy.Validate() is null,
            "Existing configurations retain pictures indefinitely by default");

        var camera = new CameraSource
        {
            Channel = 1,
            MainStream = "rtsp://127.0.0.1/live",
            Enabled = false
        };
        var settings = legacy with
        {
            Sources = [camera],
            VideoTimeoutSeconds = 300,
            AutoConnect = false
        };
        foreach (var candidate in new[] { legacy, settings })
        {
            Check((candidate with { VideoTimeoutSeconds = -1 }).Validate() is not null &&
                (candidate with { VideoTimeoutSeconds = 0 }).Validate() is null &&
                (candidate with { VideoTimeoutSeconds = int.MaxValue }).Validate() is null,
                "Video timeout validation applies to recorder templates and explicit cameras");
        }

        var directory = Path.GetFullPath(Path.Combine("artifacts", "video-timeout-tests", Guid.NewGuid().ToString("N")));
        var settingsPath = Path.Combine(directory, "settings.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = settingsPath
        }).Build();
        var store = new WebSettingsStore(new JsonAppSettingsStore(configuration));
        await store.SaveConfigurationAsync(settings, "", CancellationToken.None);
        Check(store.Load(false).Settings.VideoTimeoutSeconds == 300,
            "Video timeout survives saving and loading unified web settings");

        var other = settings with
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            ConfigurationName = "Other cameras",
            VideoTimeoutSeconds = 60
        };
        using (var viewer = new ViewerService(store, configuration))
        {
            await viewer.ConfigureAsync(new(settings, null));
            await viewer.ConfigureAsync(new(other, null));
            await viewer.ActivateAsync(settings.ConfigurationId);
        }

        using (var restarted = new ViewerService(store, configuration))
        {
            var snapshot = JsonSerializer.SerializeToElement(restarted.Snapshot());
            Check(snapshot.GetProperty("settings").GetProperty("VideoTimeoutSeconds").GetInt32() == 300,
                "Active video timeout survives a web server restart");
            var catalog = store.LoadCatalog();
            Check(catalog.Configurations.Single(c => c.ConfigurationId == other.ConfigurationId).VideoTimeoutSeconds == 60,
                "Saved collections retain independent video timeouts");
            Check(settings.Normalize().WithSources().VideoTimeoutSeconds == 300 &&
                JsonSerializer.Deserialize<ViewerSettings>(JsonSerializer.Serialize(settings))!.VideoTimeoutSeconds == 300,
                "Source conversion and portable configuration serialization preserve the timeout");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }

        Console.WriteLine("PASS: " + name);
    }
}
