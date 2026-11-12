using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

internal static class MotionActionTests
{
    public static async Task RunAsync(string directory)
    {
        var webhook = new MotionAction
        {
            Name = "Notify home automation",
            Type = MotionAction.WebhookType,
            Url = "http://127.0.0.1/events"
        };
        var driveway = new MotionZone(0, 0, .5, 1, Name: "Driveway")
        {
            Id = Guid.NewGuid().ToString("N")
        };
        var doorway = new MotionZone(.5, 0, .5, 1, Name: "Front door")
        {
            Id = Guid.NewGuid().ToString("N"),
            ActionIds = [webhook.Id, MotionAction.DefaultRecordingId]
        };
        var notifyOnly = doorway with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Side gate",
            ActionIds = [webhook.Id]
        };
        var noActions = doorway with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Observe only",
            ActionIds = []
        };
        var camera = new CameraRecordingSettings
        {
            Channel = 1,
            Zones = [driveway, doorway, notifyOnly, noActions]
        };
        var settings = new RecordingSettings
        {
            Directory = directory,
            PreCaptureSeconds = 0,
            PostCaptureSeconds = 1,
            Cameras = [camera],
            Actions = [MotionAction.DefaultRecording, webhook]
        };
        Check(settings.Validate() is null, "Named reusable actions validate");
        var legacy = JsonSerializer.Deserialize<RecordingSettings>("""
            { "Cameras": [{ "Channel": 1, "Zones": [{ "X": 0, "Y": 0, "Width": 1, "Height": 1, "Name": "Existing zone" }] }] }
            """)!;
        legacy = (legacy with
        {
            Directory = directory
        }).WithZoneIds();
        var migratedZone = legacy.Camera(1).Zones.Single();
        Check(legacy.Validate() is null && migratedZone.Name == "Existing zone" &&
            new MotionActionRouter(legacy, legacy.Camera(1)).Records(migratedZone.Id),
            "Legacy settings keep zone names and inherit recording without migration writes");

        var roundTrip = JsonSerializer.Deserialize<RecordingSettings>(JsonSerializer.Serialize(settings))!;
        Check(roundTrip.Camera(1).Zones[0].ActionIds is null &&
            roundTrip.Camera(1).Zones[3].ActionIds is { Length: 0 },
            "Settings round-trip distinguishes inherited defaults from no actions");
        var changedDefaults = camera with
        {
            DefaultActionIds = [webhook.Id]
        };
        var router = new MotionActionRouter(settings, changedDefaults);
        Check(!router.Records(driveway.Id) && router.Records(doorway.Id) &&
            router.ForZone("full-frame").Single().Id == webhook.Id,
            "Camera defaults apply to inherited zones and full-frame detection, preserving overrides");
        var renamed = doorway with
        {
            Name = "Entry"
        };
        Check(renamed.Id == doorway.Id && renamed.ActionIds!.SequenceEqual(doorway.ActionIds!),
            "Renaming a zone preserves its identity and actions");
        Check((settings with { Actions = [MotionAction.DefaultRecording] }).Validate() is not null,
            "Removing a referenced action is rejected");
        Check((settings with { Cameras = [camera with { DefaultActionIds = [webhook.Id, webhook.Id] }] }).Validate() is not null,
            "Duplicate action assignments are rejected");
        Check((settings with { Cameras = [camera with { Zones = [doorway with { Exclude = true }] }] }).Validate() is not null,
            "Exclusion zones cannot run actions");
        Check((webhook with { Payload = "{" }).Validate() is not null &&
            (webhook with { Url = "file:///tmp/event" }).Validate() is not null &&
            (webhook with { CooldownSeconds = -1 }).Validate() is not null,
            "Invalid JSON, URL schemes and cooldowns are rejected");
        var paused = settings with
        {
            Actions = [MotionAction.DefaultRecording, webhook with { Enabled = false }]
        };
        Check(new MotionActionRouter(paused, camera).ForZone(notifyOnly.Id).Length == 0,
            "Paused actions are excluded from new activations");

        var source = new ScriptedSource();
        var store = new MemoryStore();
        var publisher = new EventCollector();
        await using var recorder = new RecordingCamera(
            new ViewerSettings(), "", 1, settings, new ScriptedDetectorFactory(), source, source, store, publisher);

        await source.FrameAsync(8);
        await source.FragmentAsync();
        Check(store.Writers.IsEmpty && Started(publisher, noActions.Id).Single().ActionIds!.Length == 0,
            "Explicit no-action zones detect without starting recording or webhook actions");
        await source.FrameAsync(4);
        await source.FragmentAsync();
        Check(store.Writers.IsEmpty && Started(publisher, notifyOnly.Id).Single().ActionIds!.SequenceEqual([webhook.Id]),
            "Webhook-only zones dispatch motion actions without creating a clip");

        await source.FrameAsync(1);
        await source.FragmentAsync();
        Check(store.Writers.Count == 1, "Inherited Record action starts one clip");
        await source.FrameAsync(3);
        await source.FragmentAsync();
        var doorEvent = Started(publisher, doorway.Id).Single();
        Check(doorEvent.ActionIds!.SequenceEqual([webhook.Id]) && doorEvent.RecordingId == store.Writers.Single().Id &&
            doorEvent.Zones!.Single().Name == "Front door",
            "Entering another named zone fires its webhook during an existing recording");
        await source.FrameAsync(3);
        await source.FragmentAsync();
        Check(Started(publisher, doorway.Id).Length == 1 && store.Writers.Count == 1 &&
            store.Writers.Single().Triggers.Any(trigger => trigger.Zones!.Any(zone => zone.Id == doorway.Id)),
            "Sustained motion emits once and overlapping recording actions share one clip");

        await source.FrameAsync(4);
        await Task.Delay(1100);
        await source.FragmentAsync();
        Check(store.Writers.Single().Completed,
            "Webhook-only motion does not extend a recording's post-event tail");
        recorder.SetArming(new ArmingOverride("disarmed"));
        var count = publisher.Events.Count(value => value.Type == "motion.started");
        await source.FrameAsync(3);
        await source.FragmentAsync();
        Check(store.Writers.Count == 1 && count == publisher.Events.Count(value => value.Type == "motion.started"),
            "Disarming suppresses recording and webhook actions together");
    }

    private static CameraEvent[] Started(EventCollector publisher, string zoneId)
    {
        return [.. publisher.Events.Where(value => value.Type == "motion.started" &&
            value.Zones!.Any(zone => zone.Id == zoneId))];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }

        Console.WriteLine("PASS: " + message);
    }

    private sealed class EventCollector : ICameraEventPublisher
    {
        public ConcurrentQueue<CameraEvent> Events { get; } = new();

        public void Publish(CameraEvent value)
        {
            Events.Enqueue(value);
        }
    }

    private sealed class ScriptedDetectorFactory : IMotionDetectorFactory
    {
        public string Name => "Scripted zone detector";

        public IFrameMotionDetector Create(CameraRecordingSettings settings, TimeZoneInfo timeZone)
        {
            return new Detector(settings);
        }

        private sealed class Detector(CameraRecordingSettings settings) : IMotionDetector
        {
            public MotionSample Analyse(ReadOnlySpan<byte> pixels, int width, int height, double seconds, DateTimeOffset capturedAt)
            {
                var mask = pixels[0];
                var zones = settings.Zones.Select((zone, index) =>
                    new ZoneMotion(zone.Id, zone.Name, (mask & (1 << index)) != 0, 10)).ToArray();
                return new MotionSample(zones.Any(zone => zone.Detected), 10, false, Zones: zones);
            }
        }
    }

    private sealed class ScriptedSource : IMotionFrameSource, IEncodedVideoSource
    {
        private readonly Channel<(byte Mask, TaskCompletionSource Done)> _frames = Channel.CreateUnbounded<(byte, TaskCompletionSource)>();
        private readonly Channel<TaskCompletionSource> _fragments = Channel.CreateUnbounded<TaskCompletionSource>();

        public async Task FrameAsync(byte mask)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _frames.Writer.WriteAsync((mask, done));
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task FragmentAsync()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _fragments.Writer.WriteAsync(done);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task RunAsync(ViewerSettings connection, string password, int channel,
            Func<byte[], int, int, Task> frame, CancellationToken token)
        {
            await foreach (var (Mask, Done) in _frames.Reader.ReadAllAsync(token))
            {
                await frame([Mask], 1, 1);
                Done.SetResult();
            }
        }

        public async Task RunAsync(ViewerSettings connection, string password, int channel,
            Func<byte[], Task> initialized, Func<VideoFragment, Task> fragment, CancellationToken token)
        {
            await initialized([1]);
            await foreach (var done in _fragments.Reader.ReadAllAsync(token))
            {
                await fragment(new VideoFragment([2], 1));
                done.SetResult();
            }
        }
    }

    private sealed class MemoryStore : IRecordingEventStore
    {
        public ConcurrentBag<Writer> Writers { get; } = [];

        public Task<IEventWriter> BeginAsync(int channel, string name, byte[] initialization,
            double preSeconds, CancellationToken token, DateTimeOffset? triggeredAt = null)
        {
            var writer = new Writer();
            Writers.Add(writer);
            return Task.FromResult<IEventWriter>(writer);
        }

        public sealed class Writer : IEventWriter
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public bool Completed
            {
                get; private set;
            }
            public ConcurrentQueue<RecordingTrigger> Triggers { get; } = new();

            public Task AddTriggerAsync(RecordingTrigger trigger, CancellationToken token)
            {
                Triggers.Enqueue(trigger);
                return Task.CompletedTask;
            }

            public Task AppendAsync(VideoFragment fragment, CancellationToken token)
            {
                return Task.CompletedTask;
            }

            public Task CompleteAsync(bool interrupted, CancellationToken token)
            {
                Completed = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
