using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class CameraCapacityTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "camera-capacity-tests-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory, "settings.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = path
        }).Build();
        var sources = Enumerable.Range(1, 32).Select(channel => new CameraSource
        {
            Channel = channel,
            Name = $"Camera {channel}",
            MainStream = $"rtsp://127.0.0.1/ip{channel:00}/0",
            Enabled = false
        }).ToArray();
        var connection = new ViewerSettings
        {
            AutoConnect = false,
            Sources = sources,
            Credentials = sources.ToDictionary(camera => camera.Id, _ => new CameraCredentials("test-password"))
        };
        var recording = new RecordingSettings
        {
            Directory = Path.Combine(directory, "recordings"),
            Cameras = sources.Select(camera => new CameraRecordingSettings
            {
                Channel = camera.Channel,
                CameraId = connection.CameraId(camera.Channel),
                Sensitivity = 70
            }).ToArray()
        };

        using (var store = new JsonAppSettingsStore(configuration))
        {
            var cameras = new WebSettingsStore(store);
            await cameras.SaveConfigurationAsync(connection, "shared-test-password");
            await new RecordingSettingsStore(store).SaveAsync(recording, CancellationToken.None);
            var filters = new RecordingFilterStore(store);
            await filters.SaveAsync("default", new(null, "Camera 32", new RecordingPlaybackFilter
            {
                Channel = 32,
                Zones = [new(32, "full-frame")]
            }));
            var integrations = new IntegrationSettings(store, configuration);
            integrations.SaveWebhook(new WebhookSubscription
            {
                Url = "http://127.0.0.1/events",
                Events = ["motion.started"],
                CameraIds = sources.Select(camera => connection.CameraId(camera.Channel)).ToArray()
            }, false);

            await cameras.SaveConfigurationAsync(connection with
            {
                Sources = sources.Reverse().ToArray()
            }, "shared-test-password");
        }

        using (var restarted = new JsonAppSettingsStore(configuration))
        {
            var saved = restarted.Read().Configurations.Single();
            var restored = saved.PlaybackSettings();
            Check(restored.Sources.Length == 32 && restored.ChannelCount == 32 && saved.Credentials.Count == 32,
                "More than sixteen cameras and their credentials persist across restart");
            Check(restored.Sources[0].Id == sources[^1].Id && restored.Sources[0].Channel == 32 &&
                restored.CameraName(32) == "Camera 32" && restored.CameraId(32) == connection.CameraId(32) &&
                restored.StreamUri(32) == connection.StreamUri(32),
                "Reordering preserves internal identities, names and stream addresses");
            Check(saved.Recording!.Cameras.Single(camera => camera.Channel == 32).CameraId == connection.CameraId(32) &&
                saved.RecordingFilters.Single().Filter.Channel == 32 &&
                restarted.Read().Integrations.Webhooks!.Single().CameraIds.Length == 32,
                "Recording rules, saved filters and webhook subscriptions support larger collections");
        }

        var search = new RecordingSearch
        {
            Channel = 32,
            Zones = [new(32, "full-frame")]
        };
        Check(search.Validate() is null &&
            RecordingTriggers.IsCameraId("camera-32") && RecordingTriggers.Channel("camera-32") == 32,
            "Recording searches and legacy integration identities accept channels above sixteen");
        var invalidSearch = new RecordingSearch
        {
            Channel = 0
        };
        var invalidFilter = new RecordingPlaybackFilter
        {
            Channel = -1
        };
        Check(invalidSearch.Validate() is not null && invalidFilter.Validate() is not null &&
            !RecordingTriggers.IsCameraId("camera-0") && !RecordingTriggers.IsCameraId("camera-032") &&
            !RecordingTriggers.IsCameraId("camera-2147483648"),
            "Invalid channels and malformed legacy camera identities remain rejected");
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
