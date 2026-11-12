using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Web.Onvif;

public sealed record OnvifNotification(string CameraId, string Topic, bool State, DateTimeOffset Timestamp, bool Initialized = false);

/// <summary>Bounded, expiring PullPoint subscriptions fed by the existing event hub.</summary>
public sealed class OnvifEventService(StreamingCameraCatalog catalog, StreamingSettingsStore settings,
    IntegrationEventHub hub, TimeProvider time) : BackgroundService
{
    public const string MotionTopic = "tns1:RuleEngine/CellMotionDetector/Motion";
    public const string SignalTopic = "tns1:VideoSource/Signal";
    private const string ConcreteDialect = "http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet";
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<string, OnvifNotification> _states = new();
    private readonly Dictionary<string, HashSet<string>> _motionZones = new();

    private sealed class Subscription(string? cameraId, DateTimeOffset expires, string[] topics)
    {
        public string? CameraId { get; } = cameraId;
        public DateTimeOffset Expires { get; set; } = expires;
        public string[] Topics { get; } = topics;
        public Channel<OnvifNotification> Queue { get; } = Channel.CreateBounded<OnvifNotification>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        public SemaphoreSlim Pull { get; } = new(1, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (id, reader) = hub.Subscribe();
        var identity = Identity();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (reader.TryRead(out var value))
                {
                    Publish(value);
                }

                if (!settings.Current.Enabled || !settings.Current.OnvifEnabled || identity != Identity())
                {
                    Clear();
                    identity = Identity();
                }

                RemoveExpired();
                await Task.Delay(100, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            hub.Unsubscribe(id);
            Clear();
        }
    }

    private string Identity() => $"{catalog.Connection.Settings.ConfigurationId}/{settings.Account.Username}/{settings.Account.Password}/" +
        string.Join('|', catalog.Cameras.Select(c => $"{c.Id}/{c.MainStream}/{c.SubStream}"));

    public void Publish(CameraEvent value)
    {
        if (value.ConfigurationId != catalog.Connection.Settings.ConfigurationId)
        {
            return;
        }

        var camera = catalog.Cameras.FirstOrDefault(c => catalog.Connection.Settings.CameraId(c.Channel) == value.CameraId);
        if (camera is null)
        {
            return;
        }

        var topic = value.Type switch
        {
            "motion.started" or "motion.ended" => MotionTopic,
            "camera.online" or "camera.offline" => SignalTopic,
            _ => null
        };
        if (topic is null)
        {
            return;
        }

        var active = value.Type is "motion.started" or "camera.online";
        if (topic == MotionTopic)
        {
            lock (_gate)
            {
                if (!_motionZones.TryGetValue(camera.Id, out var zones))
                {
                    zones = [];
                    _motionZones[camera.Id] = zones;
                }

                foreach (var zone in value.Zones is { Length: > 0 } ? value.Zones.Select(z => z.Id) : ["full-frame"])
                {
                    if (active)
                    {
                        zones.Add(zone);
                    }
                    else
                    {
                        zones.Remove(zone);
                    }
                }

                active = zones.Count > 0;
            }
        }

        var notification = new OnvifNotification(camera.Id, topic, active, value.Timestamp);
        _states[$"{camera.Id}/{topic}"] = notification;
        foreach (var subscription in _subscriptions.Values.Where(s => s.Expires > time.GetUtcNow() &&
            (s.CameraId is null || s.CameraId == camera.Id) && s.Topics.Contains(topic)))
        {
            subscription.Queue.Writer.TryWrite(notification);
        }
    }

    public async Task<XElement> HandleAsync(XElement operation, string? cameraId, string? subscriptionId, CancellationToken token)
    {
        var ns = operation.Name.Namespace;
        var response = new XElement(ns + (operation.Name.LocalName + "Response"));
        switch (operation.Name.LocalName)
        {
            case "GetServiceCapabilities" when subscriptionId is null:
                response.Add(new OnvifDeviceService(catalog, settings, time).ServiceCapabilities(Events, cameraId));
                return response;
            case "GetEventProperties" when subscriptionId is null:
                response.Add(new XElement(Events + "TopicNamespaceLocation", "http://www.onvif.org/onvif/ver10/topics/topicns.xml"),
                    new XElement(Notification + "FixedTopicSet", true), TopicSet(), new XElement(Notification + "TopicExpressionDialect", ConcreteDialect),
                    new XElement(Notification + "TopicExpressionDialect", "http://docs.oasis-open.org/wsn/t-1/TopicExpression/Concrete"),
                    new XElement(Events + "MessageContentFilterDialect", ""),
                    new XElement(Events + "MessageContentSchemaLocation", "http://www.onvif.org/ver10/schema/onvif.xsd"));
                return response;
            case "CreatePullPointSubscription" when subscriptionId is null:
                var topics = ParseFilter(operation);
                var expires = Expiry(Value(operation, "InitialTerminationTime"), time.GetUtcNow().AddMinutes(5));
                string id;
                Subscription subscription;
                lock (_gate)
                {
                    RemoveExpired();
                    if (_subscriptions.Count >= 32)
                    {
                        throw new OnvifFault("ter:Action", "ter:TooManyPullPoints", "Maximum 32 PullPoint subscriptions.");
                    }

                    id = Guid.NewGuid().ToString("N");
                    subscription = new(cameraId, expires, topics);
                    _subscriptions[id] = subscription;
                    Synchronize(subscription);
                }

                response.Add(new XElement(Events + "SubscriptionReference", new XElement(Addressing + "Address",
                    catalog.ServiceUri($"subscriptions/{id}", cameraId).AbsoluteUri)),
                    new XElement(Notification + "CurrentTime", time.GetUtcNow()), new XElement(Notification + "TerminationTime", expires));
                return response;
            case "PullMessages" when subscriptionId is not null:
                var pull = Require(subscriptionId, cameraId);
                var timeout = Duration(Value(operation, "Timeout") ?? "PT30S", 0, 30);
                if (!int.TryParse(Value(operation, "MessageLimit"), out var limit) || limit is < 1 or > 128)
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidMessageLimit", "MessageLimit must be 1–128.");
                }

                if (!await pull.Pull.WaitAsync(0, token))
                {
                    throw new OnvifFault("ter:Action", "ter:PullMessages", "Only one concurrent pull is allowed per subscription.");
                }

                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var remaining = pull.Expires - time.GetUtcNow();
                    deadline.CancelAfter(remaining < timeout ? remaining : timeout);
                    if (!pull.Queue.Reader.TryPeek(out _) && timeout > TimeSpan.Zero)
                    {
                        try
                        {
                            await pull.Queue.Reader.WaitToReadAsync(deadline.Token);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                        }
                    }

                    Require(subscriptionId, cameraId);
                    response.Add(new XElement(Events + "CurrentTime", time.GetUtcNow()), new XElement(Events + "TerminationTime", pull.Expires));
                    for (var index = 0; index < limit && pull.Queue.Reader.TryRead(out var item); index++)
                    {
                        response.Add(NotificationElement(item));
                    }
                }
                finally
                {
                    pull.Pull.Release();
                }

                return response;
            case "Renew" when subscriptionId is not null:
                var renewed = Require(subscriptionId, cameraId);
                renewed.Expires = Expiry(Value(operation, "TerminationTime"), time.GetUtcNow().AddMinutes(5));
                response.Add(new XElement(Notification + "TerminationTime", renewed.Expires), new XElement(Notification + "CurrentTime", time.GetUtcNow()));
                return response;
            case "Unsubscribe" when subscriptionId is not null:
                Require(subscriptionId, cameraId);
                Remove(subscriptionId);
                return response;
            case "SetSynchronizationPoint" when subscriptionId is not null:
                Synchronize(Require(subscriptionId, cameraId));
                return response;
            default:
                throw Unsupported();
        }
    }

    private Subscription Require(string id, string? cameraId)
    {
        if (_subscriptions.TryGetValue(id, out var value) && value.CameraId == cameraId)
        {
            if (value.Expires > time.GetUtcNow())
            {
                return value;
            }

            Remove(id);
        }

        throw new OnvifFault("ter:InvalidArgVal", "ter:ResourceUnknown", "Unknown or expired subscription.");
    }

    private void Synchronize(Subscription subscription)
    {
        foreach (var camera in catalog.Cameras.Where(c => subscription.CameraId is null || subscription.CameraId == c.Id))
        {
            foreach (var topic in subscription.Topics)
            {
                if (_states.TryGetValue($"{camera.Id}/{topic}", out var item))
                {
                    subscription.Queue.Writer.TryWrite(item with { Initialized = true, Timestamp = time.GetUtcNow() });
                }
            }
        }
    }

    private DateTimeOffset Expiry(string? value, DateTimeOffset fallback)
    {
        if (value is null)
        {
            return fallback;
        }

        var expires = value.StartsWith('P') ? time.GetUtcNow() + Duration(value, 1, 3600) :
            DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var absolute) ? absolute : DateTimeOffset.MinValue;
        if (expires <= time.GetUtcNow() || expires > time.GetUtcNow().AddHours(1))
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidTerminationTime", "Termination must be within the next hour.");
        }

        return expires;
    }

    private static TimeSpan Duration(string value, int min, int max)
    {
        try
        {
            var duration = XmlConvert.ToTimeSpan(value);
            if (duration.TotalSeconds >= min && duration.TotalSeconds <= max)
            {
                return duration;
            }
        }
        catch (FormatException)
        {
        }

        throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidTimeout", $"Use an ISO duration between {min} and {max} seconds.");
    }

    private static string[] ParseFilter(XElement operation)
    {
        var filter = operation.Element(Events + "Filter");
        if (filter is null || !filter.HasElements)
        {
            return [MotionTopic, SignalTopic];
        }

        if (filter.Elements().Any(e => e.Name != Notification + "TopicExpression"))
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidFilter", "Only topic-expression filters are supported.");
        }

        var selected = new HashSet<string>();
        foreach (var expression in filter.Elements())
        {
            if (expression.Attribute("Dialect")?.Value is not (ConcreteDialect or "http://docs.oasis-open.org/wsn/t-1/TopicExpression/Concrete"))
            {
                throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidFilter", "Unsupported topic dialect.");
            }

            foreach (var term in expression.Value.Split('|', StringSplitOptions.TrimEntries))
            {
                var parts = term.Split(':', 2);
                if (parts.Length != 2 || expression.GetNamespaceOfPrefix(parts[0])?.NamespaceName != "http://www.onvif.org/ver10/topics")
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidFilter", "Use ONVIF topic names with a declared topic namespace.");
                }

                var path = parts[1].TrimEnd('/');
                foreach (var topic in new[] { MotionTopic, SignalTopic }.Where(t => t[5..] == path ||
                    path.EndsWith("//.", StringComparison.Ordinal) && t[5..].StartsWith(path[..^3] + "/", StringComparison.Ordinal)))
                {
                    selected.Add(topic);
                }
            }
        }

        if (selected.Count == 0)
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidFilter", "No supported motion or video-signal topic selected.");
        }

        return selected.ToArray();
    }

    public static XElement NotificationElement(OnvifNotification item) => new(Notification + "NotificationMessage",
        new XElement(Notification + "Topic", new XAttribute("Dialect", ConcreteDialect), item.Topic),
        new XElement(Notification + "Message", new XElement(Schema + "Message", new XAttribute("UtcTime", item.Timestamp),
            new XAttribute("PropertyOperation", item.Initialized ? "Initialized" : "Changed"),
            new XElement(Schema + "Source", new XElement(Schema + "SimpleItem", new XAttribute("Name", item.Topic == MotionTopic ? "VideoSourceConfigurationToken" : "SourceToken"),
                new XAttribute("Value", item.Topic == MotionTopic ? $"video_{item.CameraId}" : $"source_{item.CameraId}")),
                item.Topic == MotionTopic ? new XElement(Schema + "SimpleItem", new XAttribute("Name", "Rule"), new XAttribute("Value", "scv-motion")) : null),
            new XElement(Schema + "Data", new XElement(Schema + "SimpleItem", new XAttribute("Name", item.Topic == MotionTopic ? "IsMotion" : "State"), new XAttribute("Value", item.State))))));

    private static XElement TopicSet()
    {
        XNamespace topics = "http://www.onvif.org/ver10/topics";
        XElement Leaf(string name, string source, string data, bool motion) => new(name, new XAttribute(Topics + "topic", true),
            new XElement(Schema + "MessageDescription", new XAttribute("IsProperty", true),
                new XElement(Schema + "Source", new XElement(Schema + "SimpleItemDescription", new XAttribute("Name", source), new XAttribute("Type", "tt:ReferenceToken")),
                    motion ? new XElement(Schema + "SimpleItemDescription", new XAttribute("Name", "Rule"), new XAttribute("Type", "xs:string")) : null),
                new XElement(Schema + "Data", new XElement(Schema + "SimpleItemDescription", new XAttribute("Name", data), new XAttribute("Type", "xs:boolean")))));
        return new XElement(Topics + "TopicSet", new XAttribute(XNamespace.Xmlns + "xs", "http://www.w3.org/2001/XMLSchema"),
            new XElement(topics + "RuleEngine", new XElement("CellMotionDetector", Leaf("Motion", "VideoSourceConfigurationToken", "IsMotion", true))),
            new XElement(topics + "VideoSource", Leaf("Signal", "SourceToken", "State", false)));
    }

    private void RemoveExpired()
    {
        foreach (var pair in _subscriptions.Where(s => s.Value.Expires <= time.GetUtcNow()).ToArray())
        {
            Remove(pair.Key);
        }
    }

    private void Remove(string id)
    {
        if (_subscriptions.TryRemove(id, out var value))
        {
            value.Queue.Writer.TryComplete();
        }
    }

    private void Clear()
    {
        foreach (var id in _subscriptions.Keys)
        {
            Remove(id);
        }

        _states.Clear();
        lock (_gate)
        {
            _motionZones.Clear();
        }
    }
}
