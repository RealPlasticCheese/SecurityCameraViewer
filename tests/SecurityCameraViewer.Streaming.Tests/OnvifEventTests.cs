using System.Xml.Linq;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web;
using SecurityCameraViewer.Web.Onvif;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class OnvifEventTests
{
    public static async Task RunAsync()
    {
        var fixture = Create();
        var clock = new Clock(DateTimeOffset.UtcNow);
        using var events = new OnvifEventService(fixture.Catalog, fixture.Store, new IntegrationEventHub(), clock);
        var cameraId = fixture.Catalog.Cameras[0].Id;
        var created = await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription", new XElement(Events + "InitialTerminationTime", "PT60S")), cameraId, null, CancellationToken.None);
        var address = created.Descendants(Addressing + "Address").Single().Value;
        var id = address.Split('/').Last();
        Check(address.Contains(cameraId) && Guid.TryParseExact(id, "N", out _), "PullPoint subscriptions have unguessable camera-scoped addresses");
        events.Publish(new("motion.started", 1) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        events.Publish(new("motion.started", 3) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(3) });
        events.Publish(new("motion.ended", 1) { ConfigurationId = "foreign", SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        var pulled = await PullAsync(events, cameraId, id);
        Check(pulled.Elements(Notification + "NotificationMessage").Count() == 1 && pulled.Descendants(Schema + "SimpleItem").Any(e =>
            e.Attribute("Name")?.Value == "IsMotion" && e.Attribute("Value")?.Value == "true"), "Motion notifications isolate cameras and configurations");
        events.Publish(new("motion.started", 1, Zones: [new("zone-a", "A")]) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        events.Publish(new("motion.started", 1, Zones: [new("zone-b", "B")]) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        events.Publish(new("motion.ended", 1) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        events.Publish(new("motion.ended", 1, Zones: [new("zone-a", "A")]) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        var zoneState = await PullAsync(events, cameraId, id);
        Check(zoneState.Descendants(Schema + "SimpleItem").Where(e => e.Attribute("Name")?.Value == "IsMotion").Last().Attribute("Value")?.Value == "true",
            "A zone ending does not clear ONVIF motion while another zone remains active");
        events.Publish(new("motion.ended", 1, Zones: [new("zone-b", "B")]) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        var endedState = await PullAsync(events, cameraId, id);
        Check(endedState.Descendants(Schema + "SimpleItem").Single(e => e.Attribute("Name")?.Value == "IsMotion").Attribute("Value")?.Value == "false",
            "ONVIF motion ends when the final active zone ends");
        var sync = await events.HandleAsync(new XElement(Events + "SetSynchronizationPoint"), cameraId, id, CancellationToken.None);
        Check(sync.Name == Events + "SetSynchronizationPointResponse", "Synchronization requests are supported");
        var initialized = await PullAsync(events, cameraId, id);
        Check(initialized.Descendants(Schema + "Message").Any(e => e.Attribute("PropertyOperation")?.Value == "Initialized"),
            "Synchronization replays observed state as Initialized properties");
        await ThrowsAsync<OnvifFault>(async () => await PullAsync(events, fixture.Catalog.Cameras[1].Id, id), "Subscriptions cannot be pulled from a different camera endpoint");
        var renewed = await events.HandleAsync(new XElement(Notification + "Renew", new XElement(Notification + "TerminationTime", "PT120S")), cameraId, id, CancellationToken.None);
        Check(renewed.Descendants(Notification + "TerminationTime").Single().Value.Contains('T'), "WS-Notification Renew extends the subscription");
        await events.HandleAsync(new XElement(Notification + "Unsubscribe"), cameraId, id, CancellationToken.None);
        await ThrowsAsync<OnvifFault>(async () => await PullAsync(events, cameraId, id), "Unsubscribe releases the subscription immediately");

        var filter = new XElement(Events + "Filter", new XElement(Notification + "TopicExpression",
            new XAttribute("Dialect", "http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet"),
            new XAttribute(XNamespace.Xmlns + "motion", "http://www.onvif.org/ver10/topics"), "motion:RuleEngine/CellMotionDetector/Motion"));
        var filtered = await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription", filter), null, null, CancellationToken.None);
        var filterId = filtered.Descendants(Addressing + "Address").Single().Value.Split('/').Last();
        await PullAsync(events, null, filterId);
        events.Publish(new("camera.online", 1) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        events.Publish(new("motion.ended", 1) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        var filteredPull = await PullAsync(events, null, filterId);
        Check(filteredPull.Elements(Notification + "NotificationMessage").Count() == 1 && filteredPull.Descendants(Notification + "Topic").Single().Value == OnvifEventService.MotionTopic,
            "Topic filtering resolves arbitrary XML prefixes and excludes unselected signal events");
        await ThrowsAsync<OnvifFault>(async () => await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription",
            new XElement(Events + "Filter", new XElement(Notification + "MessageContent", "unsupported"))), null, null, CancellationToken.None),
            "Unsupported message-content filters are rejected explicitly");
        await ThrowsAsync<OnvifFault>(async () => await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription",
            new XElement(Events + "InitialTerminationTime", "PT7200S")), null, null, CancellationToken.None), "Subscription durations are bounded");
        await ThrowsAsync<OnvifFault>(async () => await events.HandleAsync(new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT31S"),
            new XElement(Events + "MessageLimit", 1)), null, filterId, CancellationToken.None), "Pull requests cannot hold a connection longer than 30 seconds");
        await ThrowsAsync<OnvifFault>(async () => await events.HandleAsync(new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT0S"),
            new XElement(Events + "MessageLimit", 129)), null, filterId, CancellationToken.None), "Pull response sizes are bounded");
        clock.Now = clock.Now.AddHours(2);
        await ThrowsAsync<OnvifFault>(async () => await PullAsync(events, null, filterId), "Expired subscriptions cannot be used or renewed");
        clock.Now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 32; index++)
        {
            await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription"), null, null, CancellationToken.None);
        }

        await ThrowsAsync<OnvifFault>(async () => await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription"), null, null, CancellationToken.None),
            "Subscription allocation is capped at 32 and expired slots are reclaimed");
        var properties = await events.HandleAsync(new XElement(Events + "GetEventProperties"), null, null, CancellationToken.None);
        Check(properties.Descendants(Topics + "TopicSet").Any() && properties.Descendants(Schema + "MessageDescription").Count() == 2,
            "Event properties advertise typed standard motion and video-signal topics");
    }

    private static Task<XElement> PullAsync(OnvifEventService events, string? cameraId, string id) => events.HandleAsync(
        new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT0S"), new XElement(Events + "MessageLimit", 128)), cameraId, id, CancellationToken.None);
}
