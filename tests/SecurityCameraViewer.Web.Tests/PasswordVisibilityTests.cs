using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class PasswordVisibilityTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "password-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        var camera = new CameraSource
        {
            Channel = 1,
            MainStream = "rtsp://127.0.0.1/live",
            Enabled = false
        };
        var otherCamera = camera with
        {
            Id = Guid.NewGuid().ToString("N")
        };
        var settings = new ViewerSettings
        {
            Sources = [camera],
            AutoConnect = false
        };
        var other = settings with
        {
            ConfigurationId = Guid.NewGuid().ToString("N"),
            Sources = [otherCamera]
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = settingsPath
        }).Build();
        using var store = new JsonAppSettingsStore(configuration);
        store.Update(current => current with
        {
            Configurations =
            [
                new() { Connection = settings, Password = "shared-secret", Credentials = new() { [camera.Id] = new("camera-secret") } },
                new() { Connection = other, Password = "shared-secret", Credentials = new() { [otherCamera.Id] = new("other-secret") } }
            ]
        });
        using var service = new ViewerService(new WebSettingsStore(new JsonAppSettingsStore(configuration)), configuration);
        Check(!JsonSerializer.Serialize(service.Configurations()).Contains("secret"), "Default configuration response omits passwords");
        var revealed = JsonSerializer.SerializeToElement(service.ConfigurationPasswords(settings.ConfigurationId));
        Check(revealed.GetProperty("sharedPassword").GetString() == "shared-secret" &&
            revealed.GetProperty("cameraPasswords").GetProperty(camera.Id).GetString() == "camera-secret",
            "Explicit reveal loads both shared and camera passwords");
        var otherRevealed = JsonSerializer.SerializeToElement(service.ConfigurationPasswords(other.ConfigurationId));
        Check(otherRevealed.GetProperty("cameraPasswords").GetProperty(otherCamera.Id).GetString() == "other-secret" &&
            !otherRevealed.GetProperty("cameraPasswords").TryGetProperty(camera.Id, out _),
            "Reveal returns only the selected configuration's camera credentials");
        await service.ConfigureAsync(new(other, "updated-shared", new()
        {
            [otherCamera.Id] = new("updated-camera")
        }));
        var updated = JsonSerializer.SerializeToElement(service.ConfigurationPasswords(other.ConfigurationId));
        Check(updated.GetProperty("sharedPassword").GetString() == "updated-shared" &&
            updated.GetProperty("cameraPasswords").GetProperty(otherCamera.Id).GetString() == "updated-camera",
            "Reveal uses edited and saved passwords");
        Check(File.ReadAllText(store.FilePath).Contains("updated-shared") && File.ReadAllText(store.FilePath).Contains("updated-camera"),
            "Edited passwords persist in the private settings document");
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
