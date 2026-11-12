using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using SecurityCameraViewer.Web.Onvif;
using SecurityCameraViewer.Web.Streaming;
using static SecurityCameraViewer.Streaming.Tests.TestSupport;
using static SecurityCameraViewer.Web.Onvif.OnvifSoap;

namespace SecurityCameraViewer.Streaming.Tests;

internal static class OnvifSchemaTests
{
    public static async Task RunAsync()
    {
        var directory = Path.GetFullPath("artifacts/onvif-schemas");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")))!;
        var schemas = new XmlSchemaSet
        {
            XmlResolver = new CachedResolver(directory, manifest),
            CompilationSettings = new XmlSchemaCompilationSettings { EnableUpaCheck = false }
        };
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        foreach (var (url, filename) in manifest.Where(p => p.Key.EndsWith(".wsdl", StringComparison.Ordinal)))
        {
            var document = XDocument.Load(Path.Combine(directory, filename));
            foreach (var inline in document.Descendants(xs + "schema"))
            {
                var copy = new XElement(inline);
                foreach (var attribute in document.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
                {
                    if (copy.Attribute(attribute.Name) is null)
                    {
                        copy.Add(new XAttribute(attribute));
                    }
                }

                using var source = new StringReader(copy.ToString());
                using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }, url);
                schemas.Add(XmlSchema.Read(reader, (_, args) => throw args.Exception)!);
            }
        }

        schemas.Compile();
        var fixture = Create();
        var devices = new OnvifDeviceService(fixture.Catalog, fixture.Store, TimeProvider.System);
        var media = new OnvifMediaService(fixture.Catalog);
        using var events = new OnvifEventService(fixture.Catalog, fixture.Store, new(), TimeProvider.System);
        void Validate(XElement operation)
        {
            var document = Envelope(null, new XElement(operation), operation.Name.NamespaceName + "/" + operation.Name.LocalName);
            var failures = new List<string>();
            document.Validate(schemas, (_, args) => failures.Add(args.Message));
            if (failures.Count > 0)
            {
                throw new Exception(operation.Name + ": " + string.Join(" | ", failures) + "\n" + document);
            }

            Check(true, "Official XSD validates " + operation.Name);
        }

        foreach (var operation in new[] { "GetDeviceInformation", "GetSystemDateAndTime", "GetCapabilities", "GetServices", "GetServiceCapabilities",
            "GetScopes", "GetDiscoveryMode", "GetHostname", "GetNetworkProtocols", "GetNetworkInterfaces", "GetUsers" })
        {
            Validate(devices.Handle(new XElement(Device + operation), null));
        }

        Validate(devices.Handle(new XElement(Device + "GetServices", new XElement(Device + "IncludeCapability", true)), null));
        foreach (var profile in fixture.Catalog.Profiles())
        {
            fixture.Catalog.SetMediaInfo(profile, profile.Media with { AudioCodec = "G711" });
        }

        foreach (var ns in new[] { Media, Media2 })
        {
            foreach (var operation in new[] { "GetProfiles", "GetServiceCapabilities", "GetVideoSourceConfigurations", "GetVideoEncoderConfigurations", "GetAudioSourceConfigurations", "GetAudioEncoderConfigurations", "GetMetadataConfigurations" })
            {
                Validate(media.Handle(new XElement(ns + operation), null));
            }

            foreach (var operation in new[] { "GetStreamUri", "GetSnapshotUri", "GetVideoSourceConfigurationOptions", "GetVideoEncoderConfigurationOptions",
                "GetAudioSourceConfigurationOptions", "GetAudioEncoderConfigurationOptions" })
            {
                Validate(media.Handle(new XElement(ns + operation, new XElement(ns + "ProfileToken", fixture.Catalog.Profiles()[0].Token)), null));
            }
        }

        foreach (var operation in new[] { "GetVideoSources", "GetAudioSources" })
        {
            Validate(media.Handle(new XElement(Media + operation), null));
        }

        foreach (var operation in new[] { "GetVideoSources", "GetAudioSources", "GetServiceCapabilities" })
        {
            Validate(devices.Handle(new XElement(DeviceIo + operation), null));
        }

        foreach (var operation in new[] { "GetEventProperties", "GetServiceCapabilities", "CreatePullPointSubscription" })
        {
            Validate(await events.HandleAsync(new XElement(Events + operation), null, null, CancellationToken.None));
        }

        foreach (var codec in new[] { "H265", "JPEG" })
        {
            var profile = fixture.Catalog.Profiles()[0];
            fixture.Catalog.SetMediaInfo(profile, profile.Media with { Codec = codec });
            Validate(media.Handle(new XElement(Media2 + "GetProfiles"), null));
            Validate(media.Handle(new XElement(Media + "GetProfiles"), null));
        }

        Validate(media.Handle(new XElement(Media + "GetVideoEncoderConfigurationOptions"), null));
        var subscription = await events.HandleAsync(new XElement(Events + "CreatePullPointSubscription"), null, null, CancellationToken.None);
        var id = subscription.Descendants(Addressing + "Address").Single().Value.Split('/').Last();
        events.Publish(new("motion.started", 1) { SourceId = fixture.Provider.StreamingConnection.Settings.CameraId(1) });
        Validate(await events.HandleAsync(new XElement(Events + "PullMessages", new XElement(Events + "Timeout", "PT0S"),
            new XElement(Events + "MessageLimit", 128)), null, id, CancellationToken.None));
        Validate(await events.HandleAsync(new XElement(Notification + "Renew", new XElement(Notification + "TerminationTime", "PT60S")), null, id, CancellationToken.None));
        Validate(await events.HandleAsync(new XElement(Notification + "Unsubscribe"), null, id, CancellationToken.None));
    }

    private sealed class CachedResolver(string directory, Dictionary<string, string> manifest) : XmlResolver
    {
        public override ICredentials? Credentials { set { } }
        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            return manifest.TryGetValue(absoluteUri.AbsoluteUri, out var name) ? File.OpenRead(Path.Combine(directory, name)) :
                throw new IOException("Schema dependency is not cached: " + absoluteUri);
        }
    }
}
