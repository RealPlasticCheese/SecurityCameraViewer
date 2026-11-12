using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SecurityCameraViewer.Web.Onvif;

public sealed class OnvifFault(string code, string subcode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Subcode { get; } = subcode;
}

public sealed record OnvifRequest(XDocument Document, XElement Operation, XNamespace Soap, XNamespace Addressing,
    string? MessageId);

public static class OnvifSoap
{
    public static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    public static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    public static readonly XNamespace Media2 = "http://www.onvif.org/ver20/media/wsdl";
    public static readonly XNamespace Events = "http://www.onvif.org/ver10/events/wsdl";
    public static readonly XNamespace DeviceIo = "http://www.onvif.org/ver10/deviceIO/wsdl";
    public static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";
    public static readonly XNamespace Error = "http://www.onvif.org/ver10/error";
    public static readonly XNamespace Addressing = "http://www.w3.org/2005/08/addressing";
    public static readonly XNamespace Addressing2004 = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    public static readonly XNamespace Notification = "http://docs.oasis-open.org/wsn/b-2";
    public static readonly XNamespace Topics = "http://docs.oasis-open.org/wsn/t-1";

    public static async Task<OnvifRequest> ReadAsync(Stream input, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (data.Length + count > 65536)
            {
                throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidMessage", "SOAP message exceeds 64 KiB.");
            }

            data.Write(buffer, 0, count);
        }

        data.Position = 0;
        using var reader = XmlReader.Create(data, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536, Async = true
        });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Envelope" || root.Name.Namespace != Soap12 && root.Name.Namespace != Soap11)
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidMessage", "Expected a SOAP 1.1 or SOAP 1.2 envelope.");
        }

        var bodies = root.Elements(root.Name.Namespace + "Body").ToArray();
        var operations = bodies.Length == 1 ? bodies[0].Elements().ToArray() : [];
        if (operations.Length != 1)
        {
            throw new OnvifFault("ter:InvalidArgVal", "ter:InvalidMessage", "Expected exactly one SOAP operation.");
        }

        var addressing = root.Element(root.Name.Namespace + "Header")?.Elements()
            .FirstOrDefault(e => e.Name.Namespace == Addressing || e.Name.Namespace == Addressing2004)?.Name.Namespace ?? Addressing;
        return new(document, operations[0], root.Name.Namespace, addressing,
            root.Element(root.Name.Namespace + "Header")?.Element(addressing + "MessageID")?.Value);
    }

    public static XDocument Envelope(OnvifRequest? request, XElement body, string action)
    {
        var soap = request?.Soap ?? Soap12;
        var addressing = request?.Addressing ?? Addressing;
        return new(new XElement(soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", soap), new XAttribute(XNamespace.Xmlns + "tt", Schema),
            new XAttribute(XNamespace.Xmlns + "ter", Error), new XAttribute(XNamespace.Xmlns + "a", addressing),
            new XAttribute(XNamespace.Xmlns + "tns1", "http://www.onvif.org/ver10/topics"),
            new XElement(soap + "Header", new XElement(addressing + "Action", action),
                new XElement(addressing + "To", addressing.NamespaceName + (addressing == Addressing2004 ? "/role/anonymous" : "/anonymous")),
                new XElement(addressing + "MessageID", $"urn:uuid:{Guid.NewGuid()}"),
                request?.MessageId is { } id ? new XElement(addressing + "RelatesTo", id) : null),
            new XElement(soap + "Body", body)));
    }

    public static XDocument Fault(OnvifRequest? request, OnvifFault fault)
    {
        var soap = request?.Soap ?? Soap12;
        var body = soap == Soap11
            ? new XElement(soap + "Fault", new XElement("faultcode", "s:Client"), new XElement("faultstring", fault.Message),
                new XElement("detail", new XElement(Error + "Fault", new XAttribute("Code", fault.Code), new XAttribute("Subcode", fault.Subcode))))
            : new XElement(soap + "Fault", new XElement(soap + "Code", new XElement(soap + "Value", "s:Sender"),
                new XElement(soap + "Subcode", new XElement(soap + "Value", fault.Code),
                    new XElement(soap + "Subcode", new XElement(soap + "Value", fault.Subcode)))),
                new XElement(soap + "Reason", new XElement(soap + "Text", new XAttribute(XNamespace.Xml + "lang", "en"), fault.Message)));
        return Envelope(request, body, (request?.Addressing ?? Addressing).NamespaceName + "/fault");
    }

    public static async Task WriteAsync(HttpContext context, OnvifRequest? request, XDocument document, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = request?.Soap == Soap11 ? "text/xml; charset=utf-8" : "application/soap+xml; charset=utf-8";
        await context.Response.WriteAsync(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, context.RequestAborted);
    }

    public static string? Value(XElement operation, string name) => operation.Element(operation.Name.Namespace + name)?.Value;
    public static string ResponseAction(XElement operation)
    {
        var ns = operation.Name.Namespace;
        var name = operation.Name.LocalName;
        if (ns == Events)
        {
            var port = name is "PullMessagesResponse" or "SetSynchronizationPointResponse" ? "PullPointSubscription" : "EventPortType";
            return $"{ns}/{port}/{name}";
        }

        return ns == Notification ? $"http://docs.oasis-open.org/wsn/bw-2/SubscriptionManager/{name}" : $"{ns}/{name}";
    }

    public static string MediaName(string value) => string.Concat(value.EnumerateRunes().Take(64).Select(r => r.ToString()));
    public static OnvifFault Unsupported() => new("ter:ActionNotSupported", "ter:Action", "This operation is not supported by the virtual device.");
}
