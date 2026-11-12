using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Streaming;
using SecurityCameraViewer.Web.Settings;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class TestSupport
{
    public static int Count { get; private set; }

    public static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }

        Count++;
        Console.WriteLine("PASS: " + message);
    }

    public static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            Check(true, message);
            return;
        }

        throw new Exception("FAIL: expected " + typeof(T).Name + ": " + message);
    }

    public static Task ThrowsAsync<T>(Action action, string message) where T : Exception => ThrowsAsync<T>(() =>
    {
        action();
        return Task.CompletedTask;
    }, message);

    public static Fixture Create()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "streaming-tests", Guid.NewGuid().ToString("N")));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(directory, "settings.json")
        }).Build();
        var store = new StreamingSettingsStore(new JsonAppSettingsStore(configuration));
        var main = new CameraSource
        {
            Channel = 1, Name = "Front & door <test>", MainStream = "rtsp://192.0.2.10/main?token=upstream-secret",
            SubStream = "rtsp://192.0.2.10/sub", Username = "camera-user", UseDefaultPassword = true
        };
        var secondary = new CameraSource
        {
            Channel = 3, Name = "Driveway", MainStream = "rtsp://192.0.2.11/live", Username = "individual-user"
        };
        var disabled = new CameraSource { Channel = 4, Name = "Disabled", MainStream = "rtsp://192.0.2.12/live", Enabled = false };
        var provider = new CameraProvider(new ViewerSettings
        {
            Sources = [main, secondary, disabled], AutoConnect = false,
            Credentials = new Dictionary<string, CameraCredentials> { [secondary.Id] = new("individual-secret") }
        }, "upstream-password");
        store.Save(new(store.Current with
        {
            Enabled = true, AdvertisedHost = "192.0.2.20", OnvifBaseUrl = "http://192.0.2.20:5080/"
        }, "client-secret"));
        return new(directory, configuration, store, provider, new(provider, store));
    }

    public sealed record Fixture(string Directory, IConfiguration Configuration, StreamingSettingsStore Store,
        CameraProvider Provider, StreamingCameraCatalog Catalog);

    public sealed class CameraProvider(ViewerSettings settings, string password) : IStreamingCameraProvider
    {
        public StreamingConnection StreamingConnection { get; set; } = new(settings, password);
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
