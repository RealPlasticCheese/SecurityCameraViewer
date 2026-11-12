using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Web.Onvif;

public sealed record OnvifDiscoveryStatus(bool Listening, string Message);

public sealed class OnvifDiscoveryResponder(StreamingCameraCatalog catalog, StreamingSettingsStore settings,
    OnvifDeviceService devices)
{
    public static readonly XNamespace Discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    public static readonly XNamespace Discovery2009 = "http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01";
    private static readonly XNamespace Network = "http://www.onvif.org/ver10/network/wsdl";
    private readonly uint _instanceId = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly Lock _metadataGate = new();
    private string? _metadataIdentity;
    private uint _metadataVersion;
    private long _messageNumber;

    public string?[] DeviceIds() => settings.Current.DiscoveryMode switch
    {
        "aggregate" => [null],
        "both" => [null, .. catalog.Cameras.Select(c => (string?)c.Id)],
        _ => catalog.Cameras.Select(c => (string?)c.Id).ToArray()
    };

    public byte[]? Respond(ReadOnlyMemory<byte> input)
    {
        if (input.Length > 65536)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(input.ToArray());
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root?.Name != Soap12 + "Envelope")
            {
                return null;
            }

            var body = root.Element(Soap12 + "Body");
            var operation = body?.Elements().SingleOrDefault();
            if (operation is null || operation.Name.Namespace != Discovery && operation.Name.Namespace != Discovery2009)
            {
                return null;
            }

            var ns = operation.Name.Namespace;
            var header = root.Element(Soap12 + "Header");
            var addressing = header?.Elements().FirstOrDefault(e => e.Name.Namespace == Addressing || e.Name.Namespace == Addressing2004)?.Name.Namespace ?? Addressing2004;
            var request = new OnvifRequest(document, operation, Soap12, addressing, header?.Element(addressing + "MessageID")?.Value);
            var candidates = DeviceIds();
            XElement response;
            if (operation.Name.LocalName == "Probe")
            {
                var types = operation.Element(ns + "Types");
                if (types is not null && types.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(type => !MatchesType(types, type)))
                {
                    return null;
                }

                var scopes = operation.Element(ns + "Scopes");
                if (scopes?.Attribute("MatchBy") is { } matchBy && matchBy.Value != ns.NamespaceName + "/rfc3986")
                {
                    return null;
                }

                var requested = scopes?.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
                response = new XElement(ns + "ProbeMatches", candidates.Where(id => requested.All(scope => devices.Scopes(id).Any(s => ScopeMatches(s, scope))))
                    .Select(id => Description(ns, addressing, "ProbeMatch", id)));
            }
            else if (operation.Name.LocalName == "Resolve")
            {
                var uuid = operation.Element(addressing + "EndpointReference")?.Element(addressing + "Address")?.Value;
                var matches = candidates.Where(id => $"urn:uuid:{catalog.DeviceId(id)}" == uuid).ToArray();
                if (matches.Length == 0)
                {
                    return null;
                }

                response = new XElement(ns + "ResolveMatches", Description(ns, addressing, "ResolveMatch", matches[0]));
            }
            else
            {
                return null;
            }

            return Encode(SequencedEnvelope(request, response, ns));
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public byte[] Announcement(string kind, string? cameraId)
    {
        var request = new OnvifRequest(new(), new XElement(Discovery + kind), Soap12, Addressing2004, null);
        var envelope = SequencedEnvelope(request, Description(Discovery, Addressing2004, kind, cameraId), Discovery);
        envelope.Root!.Element(Soap12 + "Header")!.Element(Addressing2004 + "To")!.Value = "urn:schemas-xmlsoap-org:ws:2005:04:discovery";
        return Encode(envelope);
    }

    private XDocument SequencedEnvelope(OnvifRequest request, XElement response, XNamespace ns)
    {
        var envelope = Envelope(request, response, ns.NamespaceName + "/" + response.Name.LocalName);
        envelope.Root!.Element(Soap12 + "Header")!.Add(new XElement(ns + "AppSequence",
            new XAttribute("InstanceId", _instanceId), new XAttribute("MessageNumber", Interlocked.Increment(ref _messageNumber))));
        return envelope;
    }

    private uint MetadataVersion()
    {
        var identity = $"{settings.Current.OnvifBaseUrl}/{settings.Current.DiscoveryMode}/{string.Join(',', catalog.Cameras.Select(c => c.Id + c.Name))}";
        lock (_metadataGate)
        {
            if (_metadataIdentity != identity)
            {
                _metadataIdentity = identity;
                _metadataVersion++;
            }

            return _metadataVersion;
        }
    }

    private XElement Description(XNamespace ns, XNamespace addressing, string kind, string? cameraId) => new(ns + kind,
        new XAttribute(XNamespace.Xmlns + "dn", Network),
        new XElement(addressing + "EndpointReference", new XElement(addressing + "Address", $"urn:uuid:{catalog.DeviceId(cameraId)}")),
        kind == "Bye" ? null : new XElement(ns + "Types", "dn:NetworkVideoTransmitter"),
        kind == "Bye" ? null : new XElement(ns + "Scopes", devices.Scopes(cameraId).Aggregate((a, b) => a + " " + b)),
        kind == "Bye" ? null : new XElement(ns + "XAddrs", catalog.ServiceUri("device_service", cameraId).AbsoluteUri),
        kind == "Bye" ? null : new XElement(ns + "MetadataVersion", MetadataVersion()));

    private static bool MatchesType(XElement element, string type)
    {
        var parts = type.Split(':', 2);
        return parts.Length == 2 && parts[1] == "NetworkVideoTransmitter" && element.GetNamespaceOfPrefix(parts[0]) == Network;
    }

    private static bool ScopeMatches(string actual, string requested) => actual == requested ||
        actual.StartsWith(requested.TrimEnd('/') + "/", StringComparison.Ordinal);
    private static byte[] Encode(XDocument document) => Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
}

/// <summary>IPv4 WS-Discovery multicast on the host's active LAN interfaces; failures leave HTTP/RTSP usable.</summary>
public sealed class OnvifDiscoveryService(StreamingSettingsStore settings, StreamingCameraCatalog catalog,
    OnvifDiscoveryResponder responder, ILogger<OnvifDiscoveryService> logger) : BackgroundService
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 3702);
    private OnvifDiscoveryStatus _status = new(false, "Discovery is disabled.");
    public OnvifDiscoveryStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!Enabled())
            {
                Volatile.Write(ref _status, new(false, "Discovery is disabled. Manual ONVIF addresses remain available when ONVIF is enabled."));
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
            {
                Volatile.Write(ref _status, new(false, "Discovery could not bind UDP 3702 or join multicast. Check interfaces, port conflicts and firewall; retrying."));
                logger.LogWarning("ONVIF discovery unavailable; retrying. Manual device addresses can still be used.");
                try
                {
                    await Task.Delay(5000, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private bool Enabled() => settings.Current is { Enabled: true, OnvifEnabled: true, DiscoveryEnabled: true };
    private string Identity() => $"{settings.Current.OnvifBaseUrl}/{settings.Current.DiscoveryMode}/{string.Join(',', catalog.Cameras.Select(c => c.Id + c.Name))}";

    private async Task ListenAsync(CancellationToken token)
    {
        var addresses = Core.OnvifClient.NetworkAddresses().Select(IPAddress.Parse).ToArray();
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 3702));
        foreach (var address in addresses)
        {
            socket.JoinMulticastGroup(Multicast.Address, address);
        }

        socket.MulticastLoopback = false;
        var identity = Identity();
        var ids = responder.DeviceIds();
        await AnnounceAsync(socket, "Hello", ids, addresses, token);
        Volatile.Write(ref _status, new(true, "Listening for ONVIF discovery on UDP 3702."));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = socket.ReceiveAsync(lifetime.Token).AsTask();
        var requests = new Dictionary<IPAddress, (DateTimeOffset Window, int Count)>();
        try
        {
            while (Enabled() && identity == Identity() && !token.IsCancellationRequested)
            {
                if (await Task.WhenAny(pending, Task.Delay(1000, token)) != pending)
                {
                    continue;
                }

                var message = await pending;
                pending = socket.ReceiveAsync(lifetime.Token).AsTask();
                var now = DateTimeOffset.UtcNow;
                var previous = requests.GetValueOrDefault(message.RemoteEndPoint.Address);
                if (previous.Window < now.AddSeconds(-1))
                {
                    previous = (now, 0);
                }

                if (requests.Count > 256)
                {
                    requests.Clear();
                }

                requests[message.RemoteEndPoint.Address] = (previous.Window, previous.Count + 1);
                if (previous.Count < 20 && responder.Respond(message.Buffer) is { } reply)
                {
                    await socket.SendAsync(reply, message.RemoteEndPoint, token);
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
            }

            using var goodbye = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await AnnounceAsync(socket, "Bye", ids, addresses, goodbye.Token);
        }
    }

    private async Task AnnounceAsync(UdpClient socket, string kind, string?[] ids, IPAddress[] addresses, CancellationToken token)
    {
        foreach (var address in addresses)
        {
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            foreach (var id in ids)
            {
                try
                {
                    await socket.SendAsync(responder.Announcement(kind, id), Multicast, token);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
