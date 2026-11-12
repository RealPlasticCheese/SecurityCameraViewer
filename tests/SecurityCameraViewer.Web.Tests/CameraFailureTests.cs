using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class CameraFailureTests
{
    public static async Task RunAsync()
    {
        await MalformedStartupAsync();
        await AvailabilityFailureAsync();
        await PlaybackFailureAsync();
        foreach (var mode in new[] { "malformed", "synchronous", "asynchronous" })
        {
            await LiveFailureAsync(mode);
        }
    }

    private static async Task MalformedStartupAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var response = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(one, deadline.Token);
                request.Append((char)one[0]);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "RTSP/1.0 401 Unauthorized\r\nWWW-Authenticate: Digest realm=\"camera\", nonce=\"one\", NONCE=\"two\"\r\n\r\n"), deadline.Token);
        }, deadline.Token);
        var directory = Path.GetFullPath(Path.Combine("artifacts", "camera-failure-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(new AppSettings
        {
            Configurations = [new() { Password = "local-only-test", Connection = new ViewerSettings
        {
            Host = "127.0.0.1", RtspPort = ((IPEndPoint)listener.LocalEndpoint).Port, ChannelCount = 1, AutoConnect = true
        } }]
        }), deadline.Token);
        using var host = new HostBuilder().ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = settingsPath,
            ["ffmpeg"] = Path.Combine(directory, "missing-ffmpeg")
        })).ConfigureServices(services =>
        {
            services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
            services.AddSingleton<WebSettingsStore>();
            services.AddSingleton<ViewerService>();
            services.AddHostedService(services => services.GetRequiredService<ViewerService>());
        }).Build();
        try
        {
            await host.StartAsync(deadline.Token);
            await response;
            var viewer = host.Services.GetRequiredService<ViewerService>();
            await UntilAsync(() => viewer.Cameras.Length == 1 || viewer.ExecuteTask!.IsCompleted, deadline.Token);
            Check(!host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested &&
                viewer.ExecuteTask is { IsCompleted: false } && viewer.Cameras.Length == 1,
                "Malformed RTSP authentication during auto-connect does not stop the host");
            using var preview = viewer.Cameras[0].SubscribePreview();
            await UntilAsync(() => viewer.Cameras[0].State.Status == "retrying", deadline.Token);
            Check(!viewer.Cameras[0].State.Live, "Decoder startup failure is a retrying camera, not a server failure");
        }
        finally
        {
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task AvailabilityFailureAsync()
    {
        var probes = 0;
        var errors = 0;
        var availability = new BatteryCameraAvailability(_ => ++probes == 1
            ? throw new InvalidOperationException("rtsp://private:secret@camera/live")
            : Task.FromResult(new ProbeResult(true, 200, "Available")), TimeSpan.FromMilliseconds(20));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await availability.WaitAsync(result =>
        {
            if (!result.Success)
            {
                errors++;
                Check(!result.Message.Contains("secret"), "Probe failures do not expose credentials");
            }
        }, deadline.Token);
        Check(probes == 2 && errors == 1, "Availability probes recover after unexpected exceptions");
    }

    private static async Task PlaybackFailureAsync()
    {
        var router = new FailingRouter();
        await using var camera = new WebCamera(new ViewerSettings(), "", 1, "missing-camera-test-ffmpeg", 0, connectionRouter: router);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await UntilAsync(() => camera.State.Status == "retrying", deadline.Token);
        Check(camera.State.Message.Contains("InvalidOperationException") && !camera.State.Message.Contains("secret"),
            "Unexpected playback errors report a safe per-camera retry state");
        await UntilAsync(() => router.Attempts >= 2, deadline.Token);
        Check(true, "Playback retries after routing exceptions and remains disposable");
    }

    private static async Task LiveFailureAsync(string mode)
    {
        var source = new FailingLiveSource(mode);
        var hub = new LiveMediaHub(source);
        await using var broken = hub.Subscribe(new ViewerSettings(), "", 1, null);
        await using var healthy = hub.Subscribe(new ViewerSettings(), "", 2, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var healthyFrames = 0;
        var readHealthy = Task.Run(async () =>
        {
            await foreach (var packet in healthy.ReadAllAsync(deadline.Token))
            {
                if (packet.Type == "fragment")
                {
                    Interlocked.Increment(ref healthyFrames);
                }
            }
        }, deadline.Token);
        var retrying = false;
        var recovered = false;
        try
        {
            await foreach (var packet in broken.ReadAllAsync(deadline.Token))
            {
                retrying |= packet.Status == "retrying";
                Check(packet.Message?.Contains("secret") != true, "Live failure diagnostics keep credentials private");
                if (packet.Type == "fragment")
                {
                    recovered = true;
                    break;
                }
            }

            Check(retrying && recovered && source.Attempts == 2 && Volatile.Read(ref healthyFrames) >= 5,
                $"Live {mode} failure retries while another camera keeps streaming");
        }
        finally
        {
            await hub.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await readHealthy;
        }
        Check(hub.SessionCount == 0, $"Live {mode} failure permits clean shutdown");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(20, token);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        Console.WriteLine("PASS: " + message);
    }

    private sealed class FailingRouter : ICameraConnectionRouter
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public CameraConnection Resolve(ViewerSettings settings, string password, int channel)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("rtsp://private:secret@camera/live");
            }
            return new(settings, password);
        }
    }

    private sealed class FailingLiveSource(string mode) : ILiveMediaSource
    {
        public int Attempts
        {
            get; private set;
        }
        public Task RunAsync(ViewerSettings settings, string password, int channel, Func<LiveMediaInitialization, Task> initialized,
            Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            if (channel == 1 && ++Attempts == 1)
            {
                return mode switch
                {
                    "synchronous" => throw new InvalidOperationException("rtsp://private:secret@camera/live"),
                    "asynchronous" => Task.FromException(new FormatException("rtsp://private:secret@camera/live")),
                    _ => FragmentedMp4.ReadAsync(new MemoryStream(new byte[8]), _ => Task.CompletedTask, fragment, token)
                };
            }
            return StreamAsync(initialized, fragment, token);
        }

        private static async Task StreamAsync(Func<LiveMediaInitialization, Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            await initialized(new([1], "video/mp4", false));
            while (true)
            {
                await fragment(new([2], 1));
                await Task.Delay(100, token);
            }
        }
    }
}
