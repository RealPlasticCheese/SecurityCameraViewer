using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SecurityCameraViewer.Core;

public sealed record OnvifDiscoveryDevice(string Id, string[] Addresses, string[] Scopes, string InterfaceAddress);
public sealed record OnvifService(string Namespace, string Address);
public sealed record OnvifMediaProfile(string Token, string Name, string? VideoSourceToken, string Service,
    string? Codec, int? Width, int? Height, double? FrameRate, int? BitrateKbps, string? StreamUri, string? Error);
public sealed record OnvifDeviceReport(string Address, DateTimeOffset InspectedAt, string? Manufacturer, string? Model,
    string? Firmware, string? SerialNumber, string? HardwareId, double? ClockOffsetSeconds,
    OnvifService[] Services, OnvifMediaProfile[] Profiles, string[] Warnings);

/// <summary>Read-only ONVIF setup: discovery, device information, Media/Media2 profiles and stream URIs.</summary>
public sealed class OnvifClient
{
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace Media2 = "http://www.onvif.org/ver20/media/wsdl";
    private static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";
    private const int MaximumResponseBytes = 1024 * 1024;

    public static bool IsServiceAddress(string? address)
    {
        return address is { Length: <= 2048 } && !address.Any(char.IsControl) &&
            Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }

    public static string[] NetworkAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString()).Distinct().ToArray();
    }

    public async Task<OnvifDiscoveryDevice[]> DiscoverAsync(string? interfaceAddress, CancellationToken cancellationToken)
    {
        var addresses = NetworkAddresses();
        if (!string.IsNullOrEmpty(interfaceAddress) && !addresses.Contains(interfaceAddress))
        {
            throw new ArgumentException("Choose a network interface on the viewer host.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var results = await Task.WhenAll((string.IsNullOrEmpty(interfaceAddress) ? addresses : [interfaceAddress])
            .Select(address => ProbeInterfaceAsync(address, deadline.Token)));
        cancellationToken.ThrowIfCancellationRequested();
        return results.SelectMany(r => r).GroupBy(d => d.Id).Select(group => new OnvifDiscoveryDevice(group.Key,
            group.SelectMany(d => d.Addresses).Distinct().ToArray(), group.SelectMany(d => d.Scopes).Distinct().ToArray(),
            string.Join(", ", group.Select(d => d.InterfaceAddress).Distinct()))).Take(128).ToArray();
    }

    internal static byte[] CreateDiscoveryProbe(string messageId)
    {
        XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
        XNamespace addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "a", addressing),
            new XAttribute(XNamespace.Xmlns + "d", discovery),
            new XAttribute(XNamespace.Xmlns + "dn", "http://www.onvif.org/ver10/network/wsdl"),
            new XElement(Soap + "Header", new XElement(addressing + "MessageID", messageId),
                new XElement(addressing + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
                new XElement(addressing + "Action", discovery.NamespaceName + "/Probe"),
                new XElement(addressing + "ReplyTo", new XElement(addressing + "Address", addressing.NamespaceName + "/role/anonymous"))),
            new XElement(Soap + "Body", new XElement(discovery + "Probe", new XElement(discovery + "Types", "dn:NetworkVideoTransmitter"))));
        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }

    internal static async Task<List<OnvifDiscoveryDevice>> ProbeInterfaceAsync(string address, CancellationToken token, IPEndPoint? destination = null)
    {
        var found = new List<OnvifDiscoveryDevice>();
        try
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Parse(address), 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(address).GetAddressBytes());
            client.Ttl = 1;
            XNamespace discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
            XNamespace addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
            var messageId = "urn:uuid:" + Guid.NewGuid();
            var bytes = CreateDiscoveryProbe(messageId);
            destination ??= new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
            await client.SendAsync(bytes, destination, token);
            // Repeat once to tolerate a lost multicast datagram.
            await client.SendAsync(bytes, destination, token);
            while (found.Count < 128)
            {
                var response = await client.ReceiveAsync(token);
                try
                {
                    var document = ParseXml(response.Buffer);
                    if (document.Descendants(addressing + "RelatesTo").FirstOrDefault()?.Value != messageId)
                    {
                        continue;
                    }

                    foreach (var match in document.Descendants(discovery + "ProbeMatch"))
                    {
                        var endpoints = (match.Element(discovery + "XAddrs")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                            .Where(IsServiceAddress).Take(8).ToArray();
                        if (endpoints.Length == 0)
                        {
                            continue;
                        }

                        var id = match.Descendants(addressing + "Address").FirstOrDefault()?.Value ?? endpoints[0];
                        var scopes = (match.Element(discovery + "Scopes")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(32).ToArray();
                        found.Add(new(id, endpoints, scopes, address));
                    }
                }
                catch (XmlException)
                {
                    // Ignore unrelated or malformed multicast replies.
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            // Other interfaces may succeed even when this interface cannot multicast.
        }

        return found;
    }

    public async Task<OnvifDeviceReport> InspectAsync(string address, string username, string password, CancellationToken cancellationToken)
    {
        if (!IsServiceAddress(address))
        {
            throw new ArgumentException("Enter a full HTTP(S) ONVIF device service address without embedded credentials.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(username, password),
            AllowAutoRedirect = false,
            UseProxy = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var warnings = new List<string>();
        var endpoint = new Uri(address);
        var offset = TimeSpan.Zero;
        double? clockOffset = null;

        async Task<XDocument> CallAsync(Uri uri, XElement operation, bool authenticate = true)
        {
            // A device cannot redirect its credentials to an unrelated host or downgrade HTTPS.
            if (!IsServiceAddress(uri.AbsoluteUri) || !uri.DnsSafeHost.Equals(endpoint.DnsSafeHost, StringComparison.OrdinalIgnoreCase) ||
                endpoint.Scheme == "https" && uri.Scheme != "https")
            {
                throw new InvalidOperationException("The device advertised a different host or an insecure service address. Enter its reachable address manually.");
            }

            var envelope = new XElement(Soap + "Envelope",
                new XElement(Soap + "Header", authenticate && username.Length > 0 ? SecurityHeader(username, password, offset) : null),
                new XElement(Soap + "Body", operation));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"application/soap+xml; charset=utf-8; action=\"{operation.Name.NamespaceName}/{operation.Name.LocalName}\"");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException("ONVIF authentication was rejected. Check the ONVIF account, permissions and device clock.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"ONVIF {operation.Name.LocalName} returned HTTP {(int)response.StatusCode}.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (output.Length + count > MaximumResponseBytes)
                {
                    throw new InvalidOperationException("The ONVIF response exceeded the 1 MB limit.");
                }

                output.Write(buffer, 0, count);
            }

            var document = ParseXml(output.ToArray());
            if (document.Descendants(Soap + "Fault").Any())
            {
                throw new InvalidOperationException($"ONVIF {operation.Name.LocalName} returned a SOAP fault. Check service support, credentials and the device clock.");
            }

            return document;
        }

        async Task<XDocument?> OptionalAsync(Uri uri, XElement operation, bool authenticate = true)
        {
            try
            {
                return await CallAsync(uri, operation, authenticate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or XmlException ||
                ex is OperationCanceledException && !deadline.IsCancellationRequested)
            {
                warnings.Add(ex is InvalidOperationException ? ex.Message : $"{operation.Name.LocalName} could not be read (network, timeout or invalid XML).");
                return null;
            }
        }

        try
        {
            var clock = await OptionalAsync(endpoint, new XElement(Device + "GetSystemDateAndTime"), false);
            var utc = clock?.Descendants(Schema + "UTCDateTime").FirstOrDefault();
            if (utc is not null)
            {
                try
                {
                    int Part(string name) => int.Parse(utc.Descendants(Schema + name).First().Value, CultureInfo.InvariantCulture);
                    offset = new DateTimeOffset(Part("Year"), Part("Month"), Part("Day"), Part("Hour"), Part("Minute"), Part("Second"), TimeSpan.Zero) - DateTimeOffset.UtcNow;
                    clockOffset = Math.Round(offset.TotalSeconds, 1);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or InvalidOperationException)
                {
                    warnings.Add("The device returned an invalid UTC clock.");
                }
            }

            var information = await OptionalAsync(endpoint, new XElement(Device + "GetDeviceInformation"));
            var servicesDocument = await OptionalAsync(endpoint, new XElement(Device + "GetServices", new XElement(Device + "IncludeCapability", false)));
            var services = servicesDocument?.Descendants(Device + "Service").Select(service => new OnvifService(
                service.Element(Device + "Namespace")?.Value ?? "", service.Element(Device + "XAddr")?.Value ?? ""))
                .Where(service => IsServiceAddress(service.Address)).Take(32).ToList() ?? [];
            if (!services.Any(s => s.Namespace == Media.NamespaceName || s.Namespace == Media2.NamespaceName))
            {
                var capabilities = await OptionalAsync(endpoint, new XElement(Device + "GetCapabilities", new XElement(Device + "Category", "Media")));
                var mediaAddress = capabilities?.Descendants(Schema + "Media").Elements(Schema + "XAddr").FirstOrDefault()?.Value;
                if (IsServiceAddress(mediaAddress))
                {
                    services.Add(new(Media.NamespaceName, mediaAddress!));
                }
            }

            var profiles = new List<OnvifMediaProfile>();
            foreach (var service in services.Where(s => s.Namespace == Media2.NamespaceName || s.Namespace == Media.NamespaceName).Distinct())
            {
                XNamespace ns = service.Namespace;
                var mediaEndpoint = new Uri(service.Address);
                var document = await OptionalAsync(mediaEndpoint, new XElement(ns + "GetProfiles",
                    ns == Media2 ? new XElement(ns + "Type", "All") : null));
                foreach (var profile in document?.Descendants(ns + "Profiles").Take(32) ?? [])
                {
                    var profileToken = profile.Attribute("token")?.Value;
                    if (string.IsNullOrEmpty(profileToken))
                    {
                        continue;
                    }

                    var video = profile.Descendants().FirstOrDefault(e => e.Name.LocalName is "VideoEncoderConfiguration" or "VideoEncoder");
                    string? VideoValue(string name) => video?.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
                    int? Number(string name) => int.TryParse(VideoValue(name), CultureInfo.InvariantCulture, out var number) ? number : null;
                    var stream = await OptionalAsync(mediaEndpoint, ns == Media2
                        ? new XElement(ns + "GetStreamUri", new XElement(ns + "Protocol", "RTSP"), new XElement(ns + "ProfileToken", profileToken))
                        : new XElement(ns + "GetStreamUri", new XElement(ns + "StreamSetup", new XElement(Schema + "Stream", "RTP-Unicast"),
                            new XElement(Schema + "Transport", new XElement(Schema + "Protocol", "RTSP"))), new XElement(ns + "ProfileToken", profileToken)));
                    var streamUri = stream?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Uri")?.Value;
                    if (Uri.TryCreate(streamUri, UriKind.Absolute, out var advertised) && advertised.Scheme == "rtsp")
                    {
                        // Credentials are supplied separately, including when firmware embeds them in its reply.
                        streamUri = new UriBuilder(advertised) { UserName = "", Password = "" }.Uri.AbsoluteUri;
                    }

                    var valid = CameraSource.ValidRtsp(streamUri);
                    profiles.Add(new(profileToken, profile.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value ?? profileToken,
                        profile.Descendants().FirstOrDefault(e => e.Name.LocalName == "SourceToken")?.Value, service.Namespace,
                        VideoValue("Encoding"), Number("Width"), Number("Height"),
                        double.TryParse(VideoValue("FrameRateLimit"), CultureInfo.InvariantCulture, out var rate) ? rate : null,
                        Number("BitrateLimit"), valid ? streamUri : null,
                        valid ? null : "No usable RTSP URI was returned. A manual stream URL can be supplied."));
                }
            }

            if (profiles.Count == 0)
            {
                warnings.Add("No media profiles were returned. Check ONVIF enablement, permissions and the device service address.");
            }

            string? Info(string name) => information?.Descendants(Device + name).FirstOrDefault()?.Value;
            return new(address, DateTimeOffset.UtcNow, Info("Manufacturer"), Info("Model"), Info("FirmwareVersion"),
                Info("SerialNumber"), Info("HardwareId"), clockOffset, services.ToArray(), profiles.ToArray(), warnings.Distinct().ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("ONVIF inspection timed out. Check the device address and network connection.");
        }
    }

    private static XElement SecurityHeader(string username, string password, TimeSpan offset)
    {
        XNamespace security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        XNamespace utility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        var nonce = RandomNumberGenerator.GetBytes(20);
        var created = (DateTimeOffset.UtcNow + offset).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var digest = SHA1.HashData(nonce.Concat(Encoding.UTF8.GetBytes(created + password)).ToArray());
        return new XElement(security + "Security", new XAttribute(Soap + "mustUnderstand", true),
            new XElement(security + "UsernameToken", new XElement(security + "Username", username),
                new XElement(security + "Password", new XAttribute("Type", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest"), Convert.ToBase64String(digest)),
                new XElement(security + "Nonce", new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"), Convert.ToBase64String(nonce)),
                new XElement(utility + "Created", created)));
    }

    private static XDocument ParseXml(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumResponseBytes
        });
        return XDocument.Load(reader);
    }
}
