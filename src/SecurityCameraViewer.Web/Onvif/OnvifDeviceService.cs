using System.Xml.Linq;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Web.Onvif;

public sealed class OnvifDeviceService(StreamingCameraCatalog catalog, StreamingSettingsStore settings, TimeProvider time)
{
    public XElement Handle(XElement operation, string? cameraId)
    {
        var ns = operation.Name.Namespace;
        var response = new XElement(ns + (operation.Name.LocalName + "Response"));
        if (ns == DeviceIo)
        {
            var cameras = catalog.Cameras.Where(c => cameraId is null || c.Id == cameraId).ToArray();
            var audio = catalog.Profiles(cameraId).Where(p => p.Media.AudioCodec is "G711" or "AAC" or "G726").Select(p => p.Camera.Id).Distinct().ToArray();
            switch (operation.Name.LocalName)
            {
                case "GetServiceCapabilities":
                    response.Add(new XElement(ns + "Capabilities", new XAttribute("VideoSources", cameras.Length),
                        new XAttribute("VideoOutputs", 0), new XAttribute("AudioSources", audio.Length), new XAttribute("AudioOutputs", 0),
                        new XAttribute("RelayOutputs", 0), new XAttribute("DigitalInputs", 0)));
                    return response;
                case "GetVideoSources":
                    response.Add(cameras.Select(c => new XElement(ns + "Token", $"source_{c.Id}")));
                    return response;
                case "GetAudioSources":
                    response.Add(audio.Select(id => new XElement(ns + "Token", $"audio_source_{id}")));
                    return response;
                default:
                    throw Unsupported();
            }
        }

        switch (operation.Name.LocalName)
        {
            case "GetDeviceInformation":
                response.Add(new XElement(ns + "Manufacturer", "SecurityCameraViewer"),
                    new XElement(ns + "Model", cameraId is null ? "Consolidated camera service" : "Virtual camera"),
                    new XElement(ns + "FirmwareVersion", typeof(OnvifDeviceService).Assembly.GetName().Version?.ToString() ?? "1.0"),
                    new XElement(ns + "SerialNumber", catalog.DeviceId(cameraId).ToString("N")),
                    new XElement(ns + "HardwareId", "SecurityCameraViewer.Web"));
                break;
            case "GetSystemDateAndTime":
                var now = time.GetUtcNow().UtcDateTime;
                response.Add(new XElement(ns + "SystemDateAndTime", new XElement(Schema + "DateTimeType", "Manual"),
                    new XElement(Schema + "DaylightSavings", false),
                    new XElement(Schema + "TimeZone", new XElement(Schema + "TZ", "UTC0")),
                    DateTimeElement("UTCDateTime", now), DateTimeElement("LocalDateTime", now)));
                break;
            case "GetServices":
                var include = Value(operation, "IncludeCapability") is "true" or "1";
                foreach (var (serviceNs, name, major) in Services())
                {
                    response.Add(new XElement(ns + "Service", new XElement(ns + "Namespace", serviceNs.NamespaceName),
                        new XElement(ns + "XAddr", catalog.ServiceUri(name, cameraId).AbsoluteUri),
                        include ? new XElement(ns + "Capabilities", ServiceCapabilities(serviceNs, cameraId)) : null,
                        new XElement(ns + "Version", new XElement(Schema + "Major", major), new XElement(Schema + "Minor", 0))));
                }

                break;
            case "GetCapabilities":
                var categories = operation.Elements(ns + "Category").Select(e => e.Value).ToArray();
                var all = categories.Length == 0 || categories.Contains("All");
                if (categories.Any(c => c is not ("All" or "Device" or "Media" or "Events" or "PTZ" or "Imaging" or "Analytics")))
                {
                    throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidCategory", "Unknown capability category.");
                }

                if (!all && !categories.Any(c => c is "Device" or "Media" or "Events"))
                {
                    throw new OnvifFault("ter:ActionNotSupported", "ter:NoSuchService", "Requested service is not available.");
                }

                response.Add(new XElement(ns + "Capabilities",
                    all || categories.Contains("Device") ? DeviceCapabilities(cameraId) : null,
                    all || categories.Contains("Events") ? new XElement(Schema + "Events",
                        new XElement(Schema + "XAddr", catalog.ServiceUri("events_service", cameraId).AbsoluteUri),
                        new XElement(Schema + "WSSubscriptionPolicySupport", false),
                        new XElement(Schema + "WSPullPointSupport", true), new XElement(Schema + "WSPausableSubscriptionManagerInterfaceSupport", false)) : null,
                    all || categories.Contains("Media") ? new XElement(Schema + "Media",
                        new XElement(Schema + "XAddr", catalog.ServiceUri("media_service", cameraId).AbsoluteUri),
                        new XElement(Schema + "StreamingCapabilities", new XElement(Schema + "RTPMulticast", false),
                            new XElement(Schema + "RTP_TCP", false), new XElement(Schema + "RTP_RTSP_TCP", true)),
                        new XElement(Schema + "Extension", new XElement(Schema + "ProfileCapabilities",
                            new XElement(Schema + "MaximumNumberOfProfiles", catalog.Profiles(cameraId).Length)))) : null,
                    all ? new XElement(Schema + "Extension", new XElement(Schema + "DeviceIO",
                        new XElement(Schema + "XAddr", catalog.ServiceUri("deviceio_service", cameraId).AbsoluteUri),
                        new XElement(Schema + "VideoSources", catalog.Profiles(cameraId).Select(p => p.Camera.Id).Distinct().Count()),
                        new XElement(Schema + "VideoOutputs", 0), new XElement(Schema + "AudioSources", catalog.Profiles(cameraId).Where(p => p.Media.AudioCodec is "G711" or "AAC" or "G726").Select(p => p.Camera.Id).Distinct().Count()),
                        new XElement(Schema + "AudioOutputs", 0), new XElement(Schema + "RelayOutputs", 0))) : null));
                break;
            case "GetServiceCapabilities":
                response.Add(ServiceCapabilities(Device, cameraId));
                break;
            case "GetScopes":
                response.Add(Scopes(cameraId).Select(s => new XElement(ns + "Scopes", new XElement(Schema + "ScopeDef", "Fixed"), new XElement(Schema + "ScopeItem", s))));
                break;
            case "GetDiscoveryMode":
                response.Add(new XElement(ns + "DiscoveryMode", settings.Current.DiscoveryEnabled ? "Discoverable" : "NonDiscoverable"));
                break;
            case "GetHostname":
                response.Add(new XElement(ns + "HostnameInformation", new XElement(Schema + "FromDHCP", false), new XElement(Schema + "Name", settings.Current.AdvertisedHost)));
                break;
            case "GetNetworkProtocols":
                response.Add(NetworkProtocol("HTTP", new Uri(settings.Current.OnvifBaseUrl).Port), NetworkProtocol("RTSP", settings.Current.RtspPort));
                break;
            case "GetNetworkInterfaces":
                var addresses = Core.OnvifClient.NetworkAddresses();
                response.Add(addresses.Select((address, index) => new XElement(ns + "NetworkInterfaces", new XAttribute("token", $"interface-{index}"),
                    new XElement(Schema + "Enabled", true), new XElement(Schema + "IPv4", new XElement(Schema + "Enabled", true),
                        new XElement(Schema + "Config", new XElement(Schema + "Manual", new XElement(Schema + "Address", address),
                            new XElement(Schema + "PrefixLength", PrefixLength(address))), new XElement(Schema + "DHCP", false))))));
                break;
            case "GetUsers":
                response.Add(new XElement(ns + "User", new XElement(Schema + "Username", settings.Current.Username), new XElement(Schema + "UserLevel", "User")));
                break;
            default:
                throw Unsupported();
        }

        return response;
    }

    public string[] Scopes(string? cameraId)
    {
        var name = cameraId is null ? "Security Camera Viewer" : catalog.Cameras.First(c => c.Id == cameraId).Name;
        // No Profile S/T conformance scope: this service implements a documented subset.
        return ["onvif://www.onvif.org/type/video_encoder", "onvif://www.onvif.org/location/", $"onvif://www.onvif.org/name/{Uri.EscapeDataString(name)}",
            "onvif://www.onvif.org/hardware/SecurityCameraViewer"];
    }

    public XElement ServiceCapabilities(XNamespace ns, string? cameraId)
    {
        if (ns == Device)
        {
            return new XElement(Device + "Capabilities", new XElement(Device + "Network", new XAttribute("IPFilter", false), new XAttribute("ZeroConfiguration", false),
                new XAttribute("IPVersion6", false), new XAttribute("DynDNS", false)),
                new XElement(Device + "Security", new XAttribute("TLS1.2", false), new XAttribute("UsernameToken", true),
                    new XAttribute("HttpDigest", true), new XAttribute("AccessPolicyConfig", false), new XAttribute("DefaultAccessPolicy", false)),
                new XElement(Device + "System", new XAttribute("DiscoveryResolve", true), new XAttribute("DiscoveryBye", true),
                    new XAttribute("RemoteDiscovery", false), new XAttribute("SystemBackup", false), new XAttribute("SystemLogging", false), new XAttribute("FirmwareUpgrade", false)));
        }

        if (ns == Events)
        {
            return new XElement(Events + "Capabilities", new XAttribute("WSSubscriptionPolicySupport", false), new XAttribute("WSPullPointSupport", true),
                new XAttribute("WSPausableSubscriptionManagerInterfaceSupport", false), new XAttribute("MaxNotificationProducers", 0),
                new XAttribute("MaxPullPoints", 32), new XAttribute("PersistentNotificationStorage", false));
        }

        if (ns == DeviceIo)
        {
            return Handle(new XElement(DeviceIo + "GetServiceCapabilities"), cameraId).Elements().Single();
        }

        return OnvifMediaService.Capabilities(ns, catalog.Profiles(cameraId).Length);
    }

    private XElement DeviceCapabilities(string? cameraId) => new(Schema + "Device",
        new XElement(Schema + "XAddr", catalog.ServiceUri("device_service", cameraId).AbsoluteUri),
        new XElement(Schema + "Network", new XElement(Schema + "IPFilter", false), new XElement(Schema + "ZeroConfiguration", false),
            new XElement(Schema + "IPVersion6", false), new XElement(Schema + "DynDNS", false)),
        new XElement(Schema + "System", new XElement(Schema + "DiscoveryResolve", true), new XElement(Schema + "DiscoveryBye", true),
            new XElement(Schema + "RemoteDiscovery", false), new XElement(Schema + "SystemBackup", false), new XElement(Schema + "SystemLogging", false),
            new XElement(Schema + "FirmwareUpgrade", false), new XElement(Schema + "SupportedVersions", new XElement(Schema + "Major", 2), new XElement(Schema + "Minor", 0))),
        new XElement(Schema + "Security", new XElement(Schema + "TLS1.1", false), new XElement(Schema + "TLS1.2", false), new XElement(Schema + "OnboardKeyGeneration", false),
            new XElement(Schema + "AccessPolicyConfig", false), new XElement(Schema + "X.509Token", false), new XElement(Schema + "SAMLToken", false),
            new XElement(Schema + "KerberosToken", false), new XElement(Schema + "RELToken", false)));

    private static IEnumerable<(XNamespace Namespace, string Name, int Major)> Services() =>
        [(Device, "device_service", 2), (Media, "media_service", 1), (Media2, "media2_service", 2), (Events, "events_service", 2), (DeviceIo, "deviceio_service", 1)];

    private static XElement DateTimeElement(string name, DateTime date) => new(Schema + name,
        new XElement(Schema + "Time", new XElement(Schema + "Hour", date.Hour), new XElement(Schema + "Minute", date.Minute), new XElement(Schema + "Second", date.Second)),
        new XElement(Schema + "Date", new XElement(Schema + "Year", date.Year), new XElement(Schema + "Month", date.Month), new XElement(Schema + "Day", date.Day)));

    private static XElement NetworkProtocol(string name, int port) => new(Device + "NetworkProtocols",
        new XElement(Schema + "Name", name), new XElement(Schema + "Enabled", true), new XElement(Schema + "Port", port));

    private static int PrefixLength(string address) => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.ToString() == address)?.PrefixLength ?? 32;
}
