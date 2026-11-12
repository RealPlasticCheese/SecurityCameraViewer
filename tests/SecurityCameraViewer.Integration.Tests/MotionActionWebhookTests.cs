using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Settings;

internal static class MotionActionWebhookTests
{
    public static async Task RunAsync(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["settings"] = Path.Combine(root, "zone-actions", "settings.json")
        }).Build();
        using var settingsStore = new JsonAppSettingsStore(configuration);
        var integrations = new IntegrationSettings(settingsStore, configuration);
        var catalog = new ActionCatalog();
        using var receiver = new TcpListener(IPAddress.Loopback, 0);
        receiver.Start();
        var port = ((IPEndPoint)receiver.LocalEndpoint).Port;
        var action = new MotionAction
        {
            Name = "Door notification",
            Type = MotionAction.WebhookType,
            Url = $"http://127.0.0.1:{port}/events",
            CooldownSeconds = 60
        };
        var custom = action with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Turn on light",
            Payload = "{\n  \"message\": \"Entrée\", \"state\": \"on\"\n}"
        };
        catalog.Actions[action.Id] = action;
        catalog.Actions[custom.Id] = custom;
        var recordingStore = new RecordingSettingsStore(settingsStore);
        await recordingStore.SaveAsync(new RecordingSettings
        {
            Directory = Path.Combine(root, "recordings"),
            Actions = [MotionAction.DefaultRecording, action, custom]
        }, CancellationToken.None);
        Check(recordingStore.Load().Actions.Single(item => item.Id == custom.Id).Payload == custom.Payload,
            "Recording settings persist reusable action definitions and exact custom bodies");
        var requests = new ConcurrentQueue<(string Id, string ActionId, string Body)>();
        var respondSuccessfully = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receiverTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await receiver.AcceptTcpClientAsync(stop.Token);
                    using var stream = client.GetStream();
                    var headerBytes = new List<byte>();
                    var single = new byte[1];
                    while (true)
                    {
                        await stream.ReadExactlyAsync(single, stop.Token);
                        headerBytes.Add(single[0]);
                        if (headerBytes.Count >= 4 && headerBytes.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray()))
                        {
                            break;
                        }
                    }

                    var headers = Encoding.ASCII.GetString([.. headerBytes]).Split("\r\n")
                        .Skip(1).Where(line => line.Contains(':'))
                        .Select(line => line.Split(':', 2))
                        .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var body = new byte[int.Parse(headers["Content-Length"])];
                    await stream.ReadExactlyAsync(body, stop.Token);
                    requests.Enqueue((headers["X-SCV-Delivery"], headers["X-SCV-Action"], Encoding.UTF8.GetString(body)));
                    var status = Volatile.Read(ref respondSuccessfully) ? "204 No Content" : "503 Unavailable";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        });

        try
        {
            var front = new EventZone(Guid.NewGuid().ToString("N"), "Front door");
            var side = new EventZone(Guid.NewGuid().ToString("N"), "Side gate");
            var motion = new CameraEvent("motion.started", 1, "Outside", [front], "existing-recording")
            {
                ActionIds = [action.Id, custom.Id]
            };
            var hub = new IntegrationEventHub();
            using (var dispatcher = new WebhookDispatcher(integrations, hub, catalog))
            {
                await dispatcher.StartAsync(stop.Token);
                hub.Publish(motion);
                hub.Publish(motion with
                {
                    Id = Guid.NewGuid().ToString("N")
                });
                hub.Publish(motion with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Zones = [side],
                    ActionIds = [action.Id]
                });
                await UntilAsync(() => dispatcher.List().Length == 3 && dispatcher.List().Count(item => item.Attempts >= 1) == 2);
                Check(dispatcher.PendingCount == 3,
                    "Zone actions queue independently without enabling API sign-in or legacy subscriptions");
                Check(dispatcher.List().Count(item => item.ActionId == action.Id) == 2,
                    "Action cooldown suppresses repeated activation but allows another zone to notify");
                var (Id, ActionId, Body) = requests.First(request => request.ActionId == custom.Id);
                Check(Body == custom.Payload,
                    "Custom JSON bodies are delivered exactly, including Unicode and whitespace");
                using var body = JsonDocument.Parse(requests.First(request => request.ActionId == action.Id).Body);
                Check(body.RootElement.GetProperty("cameraName").GetString() == "Outside" &&
                    body.RootElement.GetProperty("zones")[0].GetProperty("name").GetString() == "Front door" &&
                    body.RootElement.GetProperty("recordingId").GetString() == "existing-recording",
                    "Default webhook bodies identify named cameras, zones and the current recording");
                await dispatcher.StopAsync(stop.Token);
            }

            Volatile.Write(ref respondSuccessfully, true);
            hub = new IntegrationEventHub();
            using (var restarted = new WebhookDispatcher(integrations, hub, catalog))
            {
                Check(restarted.PendingCount == 3, "Action deliveries survive server restart");
                await restarted.StartAsync(stop.Token);
                hub.Publish(motion with
                {
                    Id = Guid.NewGuid().ToString("N")
                });
                await UntilAsync(() => restarted.List().All(item => item.Status == "delivered"));
                await restarted.StopAsync(stop.Token);
                Check(restarted.List().Length == 3 && requests.Select(request => request.Id).Distinct().Count() == 3 &&
                    restarted.List().Count(item => item.Attempts >= 2) == 2,
                    "Retries preserve delivery IDs and cooldowns survive restart");
            }

            catalog.Actions[action.Id] = action with
            {
                Enabled = false
            };
            hub = new IntegrationEventHub();
            using var paused = new WebhookDispatcher(integrations, hub, catalog);
            await paused.StartAsync(stop.Token);
            hub.Publish(motion with
            {
                Id = Guid.NewGuid().ToString("N"),
                Channel = 2,
                ActionIds = [action.Id]
            });
            await paused.StopAsync(stop.Token);
            Check(paused.List().Length == 3, "Paused actions do not admit new webhook deliveries");
        }
        finally
        {
            await stop.CancelAsync();
            await receiverTask;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15))
            {
                throw new TimeoutException("Webhook action test did not complete.");
            }

            await Task.Delay(25);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }

        Console.WriteLine("PASS: " + message);
    }

    private sealed class ActionCatalog : IMotionActionCatalog
    {
        public ConcurrentDictionary<string, MotionAction> Actions { get; } = new();

        public MotionAction? FindAction(string id)
        {
            return Actions.GetValueOrDefault(id);
        }
    }
}
