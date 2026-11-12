using System.Text;
using System.Xml;
using System.Xml.Linq;
using SecurityCameraViewer.Web.Onvif;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class OnvifProtocolTests
{
    public static async Task RunAsync()
    {
        var fixture = Create();
        var clock = new Clock(new(2026, 10, 7, 4, 5, 6, TimeSpan.Zero));
        var device = new OnvifDeviceService(fixture.Catalog, fixture.Store, clock);
        var media = new OnvifMediaService(fixture.Catalog);
        var cameraId = fixture.Catalog.Cameras[0].Id;
        foreach (var operation in new[] { "GetDeviceInformation", "GetSystemDateAndTime", "GetCapabilities", "GetServices", "GetScopes",
            "GetDiscoveryMode", "GetHostname", "GetNetworkProtocols", "GetNetworkInterfaces", "GetUsers", "GetServiceCapabilities" })
        {
            var result = device.Handle(new XElement(Device + operation), cameraId);
            Check(result.Name == Device + operation + "Response" && !result.ToString().Contains("secret"),
                "Device operation is namespaced and redacted: " + operation);
        }

        var services = device.Handle(new XElement(Device + "GetServices", new XElement(Device + "IncludeCapability", true)), cameraId);
        Check(services.Elements().Count() == 5 && services.Descendants(Device + "XAddr").All(e => e.Value.Contains(cameraId)),
            "Service enumeration scopes every endpoint to the selected virtual camera");
        Check(services.Elements(Device + "Service").All(s => s.Element(Device + "Capabilities")?.HasElements == true), "IncludeCapability supplies each advertised service's capabilities");
        var utc = device.Handle(new XElement(Device + "GetSystemDateAndTime"), null);
        Check(utc.Descendants(Schema + "Hour").First().Value == "4", "Anonymous clock bootstrap uses actual UTC");
        await ThrowsAsync<OnvifFault>(() => device.Handle(new XElement(Device + "SystemReboot"), null), "Virtual devices reject reboot and upstream mutations");
        var capabilities = device.Handle(new XElement(Device + "GetCapabilities"), null);
        Check(!capabilities.Descendants(Schema + "PTZ").Any() && !capabilities.Descendants(Schema + "Recording").Any(),
            "Capabilities do not claim PTZ or recording playback that is not implemented");

        var profiles = media.Handle(new XElement(Media + "GetProfiles"), cameraId);
        Check(profiles.Elements(Media + "Profiles").Count() == 2 && profiles.ToString().Contains("Front &amp; door &lt;test&gt;"),
            "Virtual camera returns main/sub profiles and escapes camera names in XML");
        var profile = fixture.Catalog.Profiles(cameraId)[0];
        foreach (var ns in new[] { Media, Media2 })
        {
            var uri = media.Handle(new XElement(ns + "GetStreamUri", new XElement(ns + "ProfileToken", profile.Token)), cameraId);
            Check(uri.Descendants().First(e => e.Name.LocalName == "Uri").Value == fixture.Catalog.StreamUri(profile).AbsoluteUri,
                "Media/Media2 returns relay RTSP addresses with stable profile tokens");
            var snapshot = media.Handle(new XElement(ns + "GetSnapshotUri", new XElement(ns + "ProfileToken", profile.Token)), cameraId);
            Check(snapshot.Descendants().First(e => e.Name.LocalName == "Uri").Value.Contains("/snapshot/" + profile.Token),
                "Snapshots use authenticated camera-specific endpoints");
            foreach (var operation in new[] { "GetVideoSourceConfigurations", "GetVideoEncoderConfigurations", "GetServiceCapabilities", "GetAudioEncoderConfigurations" })
            {
                Check(media.Handle(new XElement(ns + operation), cameraId).Name == ns + operation + "Response", "Media configuration queries work: " + ns + operation);
            }
        }

        await ThrowsAsync<OnvifFault>(() => media.Handle(new XElement(Media + "GetStreamUri", new XElement(Media + "ProfileToken", fixture.Catalog.Profiles()[2].Token)), cameraId),
            "Cross-camera profile tokens are rejected at virtual endpoints");
        await ThrowsAsync<OnvifFault>(() => media.Handle(new XElement(Media + "GetStreamUri", new XElement(Media + "ProfileToken", "missing")), null),
            "Unknown profiles return ONVIF NoProfile faults");
        await ThrowsAsync<OnvifFault>(() => media.Handle(new XElement(Media + "SetVideoEncoderConfiguration"), null),
            "Encoder mutations return explicit faults rather than pretend success");
        await ThrowsAsync<OnvifFault>(() => media.Handle(new XElement(Media2 + "GetStreamUri", new XElement(Media2 + "ProfileToken", profile.Token), new XElement(Media2 + "Protocol", "HTTP")), null),
            "Unsupported stream transports are rejected");
        fixture.Catalog.SetMediaInfo(profile, new("H265", 2560, 1440, 25, 6000, "G711"));
        Check(media.Handle(new XElement(Media + "GetProfiles"), cameraId).Elements().Count() == 1,
            "Media1 excludes H.265 encoders that its schema cannot represent");
        var media2 = media.Handle(new XElement(Media2 + "GetProfiles"), cameraId);
        Check(media2.Elements().Count() == 2 && media2.Descendants(Schema + "Encoding").Any(e => e.Value == "H265") &&
            media2.Descendants(Media2 + "AudioEncoder").Any(), "Media2 advertises actual H.265 encoding and probed audio configuration");
        var audioOptions = media.Handle(new XElement(Media2 + "GetAudioEncoderConfigurationOptions", new XElement(Media2 + "ProfileToken", profile.Token)), cameraId);
        Check(audioOptions.Descendants(Schema + "Encoding").Single().Value == "PCMU" && audioOptions.Descendants(Schema + "Items").Count() == 2,
            "Media2 advertises MIME audio encodings and bounded bitrate/sample-rate options");
        await ThrowsAsync<OnvifFault>(() => media.Handle(new XElement(Media2 + "GetAudioEncoderConfigurations",
            new XElement(Media2 + "ConfigurationToken", "missing")), cameraId), "Audio configuration filters reject unknown tokens");

        foreach (var soap in new[] { Soap11, Soap12 })
        {
            var document = new XDocument(new XElement(soap + "Envelope", new XElement(soap + "Header",
                new XElement(Addressing + "MessageID", "urn:uuid:test")), new XElement(soap + "Body", new XElement(Media + "GetProfiles"))));
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
            var parsed = await ReadAsync(input, CancellationToken.None);
            var response = Envelope(parsed, new XElement(Media + "GetProfilesResponse"), Media.NamespaceName + "/GetProfilesResponse");
            Check(response.Root?.Name == soap + "Envelope" && response.Descendants(Addressing + "RelatesTo").Single().Value == "urn:uuid:test",
                "SOAP versions and WS-Addressing correlation survive request parsing");
            Check(Fault(parsed, new("ter:InvalidArgVal", "ter:NoProfile", "Bad <profile>")).Descendants(soap + "Fault").Any(),
                "Protocol faults preserve SOAP 1.1/1.2 envelopes");
        }

        await ParseFailureAsync<XmlException>("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///password.txt'>]><x>&e;</x>", "External entities and DTDs are prohibited");
        await ParseFailureAsync<OnvifFault>(new string('x', 65537), "Oversized SOAP bodies are rejected before XML parsing");
        await ParseFailureAsync<OnvifFault>($"<s:Envelope xmlns:s='{Soap12}'><s:Body><a/><b/></s:Body></s:Envelope>", "Multiple SOAP operations are rejected");

        var discovery = new OnvifDiscoveryResponder(fixture.Catalog, fixture.Store, device);
        var probe = DiscoveryProbe();
        var resultBytes = discovery.Respond(Encoding.UTF8.GetBytes(probe.ToString()));
        Check(resultBytes is not null && XDocument.Parse(Encoding.UTF8.GetString(resultBytes)).Descendants(OnvifDiscoveryResponder.Discovery + "ProbeMatch").Count() == 2,
            "Discovery advertises individual enabled cameras by default");
        var discovered = XDocument.Parse(Encoding.UTF8.GetString(resultBytes!));
        Check(discovered.Descendants(OnvifDiscoveryResponder.Discovery + "XAddrs").All(e => e.Value.Contains("/onvif/cameras/")),
            "Individual discovery advertises each camera's dedicated device endpoint");
        fixture.Store.Save(new(fixture.Store.Current with { DiscoveryMode = "both" }, null));
        Check(discovery.DeviceIds().Length == 3, "Both discovery mode includes the recorder and individual cameras");
        fixture.Store.Save(new(fixture.Store.Current with { DiscoveryMode = "aggregate" }, null));
        Check(discovery.DeviceIds().Length == 1 && discovery.DeviceIds()[0] is null, "Recorder discovery produces exactly one multi-camera device");
        Check(discovery.Respond(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e 'x'>]><x>&e;</x>")) is null,
            "Malformed discovery datagrams are discarded safely");
        var unknown = DiscoveryProbe("dn:Unknown");
        Check(discovery.Respond(Encoding.UTF8.GetBytes(unknown.ToString())) is null, "Discovery ignores probes for unsupported device types");
        var ns2009 = probe.ToString().Replace(OnvifDiscoveryResponder.Discovery.NamespaceName, OnvifDiscoveryResponder.Discovery2009.NamespaceName);
        Check(discovery.Respond(Encoding.UTF8.GetBytes(ns2009)) is not null, "Both WS-Discovery versions are supported");
        var resolve = new XDocument(new XElement(Soap12 + "Envelope", new XElement(Soap12 + "Body",
            new XElement(OnvifDiscoveryResponder.Discovery + "Resolve", new XElement(Addressing2004 + "EndpointReference",
                new XElement(Addressing2004 + "Address", $"urn:uuid:{fixture.Catalog.DeviceId(null)}"))))));
        Check(discovery.Respond(Encoding.UTF8.GetBytes(resolve.ToString())) is not null, "Discovery Resolve finds stable endpoint identities");
        var bye = XDocument.Parse(Encoding.UTF8.GetString(discovery.Announcement("Bye", null)));
        Check(bye.Descendants(OnvifDiscoveryResponder.Discovery + "Bye").Any() && !bye.Descendants(OnvifDiscoveryResponder.Discovery + "XAddrs").Any(),
            "Bye announcements use discovery endpoint identity without stale service metadata");
        var sequence = bye.Descendants(OnvifDiscoveryResponder.Discovery + "AppSequence").Single();
        Check((uint)sequence.Attribute("InstanceId")! > 1 && (long)sequence.Attribute("MessageNumber")! >
            (long)discovered.Descendants(OnvifDiscoveryResponder.Discovery + "AppSequence").Single().Attribute("MessageNumber")!,
            "Discovery announcements and responses share a restart-specific increasing message sequence");
        var hello = XDocument.Parse(Encoding.UTF8.GetString(discovery.Announcement("Hello", null)));
        Check((uint)hello.Descendants(OnvifDiscoveryResponder.Discovery + "MetadataVersion").Single() >
            (uint)discovered.Descendants(OnvifDiscoveryResponder.Discovery + "MetadataVersion").First(),
            "Changed discovery metadata advances its version for client caches");
    }

    private static XDocument DiscoveryProbe(string type = "dn:NetworkVideoTransmitter") => new(new XElement(Soap12 + "Envelope",
        new XElement(Soap12 + "Header", new XElement(Addressing2004 + "MessageID", "urn:uuid:probe")),
        new XElement(Soap12 + "Body", new XElement(OnvifDiscoveryResponder.Discovery + "Probe",
            new XAttribute(XNamespace.Xmlns + "dn", "http://www.onvif.org/ver10/network/wsdl"), new XElement(OnvifDiscoveryResponder.Discovery + "Types", type)))));

    private static async Task ParseFailureAsync<T>(string xml, string message) where T : Exception
    {
        await ThrowsAsync<T>(async () =>
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            await ReadAsync(input, CancellationToken.None);
        }, message);
    }
}
