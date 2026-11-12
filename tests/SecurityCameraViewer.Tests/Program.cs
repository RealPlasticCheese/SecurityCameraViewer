using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SecurityCameraViewer.Core;

if (FfmpegLifecycleTests.IsSource(args))
{
    return await FfmpegLifecycleTests.SourceAsync();
}

if (args.Length > 0 && args[0] == "--probe")
{
    var passwordFile = args.Length > 1 ? args[1] : "password.txt";
    var settings = new ViewerSettings { PathTemplate = args.Length > 2 ? args[2] : "/ip{channel:00}/{stream}" };
    var result = await RtspProbe.DescribeAsync(settings, File.ReadAllText(passwordFile).TrimEnd('\r', '\n'));
    Console.WriteLine($"{result.StatusCode}: {result.Message} {result.Codec}");
    return result.Success ? 0 : 1;
}

await StreamProcessingTests.JpegAsync();
if (args.Contains("--characterization-only"))
{
    return 0;
}
await StreamProcessingTests.PumpAsync();
await FfmpegLifecycleTests.RunAsync();
if (args.Contains("--performance-only"))
{
    return 0;
}

await ProcessingMetricsTests.RunAsync();
if (args.Contains("--profiling-only"))
{
    return 0;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception("FAIL: " + name);
    }

    passed++;
    Console.WriteLine("PASS: " + name);
}

var settingsDefault = new ViewerSettings();
foreach (var json in new[] { "{}", "null", "[]", "{\"streams\":null}", "{\"streams\":[null]}" })
{
    Check(MediaInspector.Parse(json).Error is not null, "Incomplete diagnostic metadata reports unavailable properties");
}
Check(MediaInspector.Parse("{\"streams\":[{\"codec_name\":\"h264\",\"width\":\"invalid\",\"height\":720}]}") is
    { Codec: "h264", Width: null, Height: 720, Error: null }, "Invalid diagnostic dimensions do not discard usable stream properties");
Check(settingsDefault.StreamUri(2).AbsoluteUri == "rtsp://192.0.2.10:554/ip02/0", "NVR path and channel numbering");
Check((settingsDefault with { Substream = true }).ExpandPath(3) == "/ip03/1", "Substream selection");
Check((settingsDefault with { PathTemplate = "/live/ch{channel0}?stream={stream}" }).StreamUri(1).AbsoluteUri.EndsWith("/live/ch0?stream=0"), "Zero-based channels and query parameters");
Check((settingsDefault with { Host = "http://192.168.1.1" }).Validate() is not null, "Reject scheme in host");
Check((settingsDefault with { PathTemplate = "/{invalid}" }).Validate() is not null, "Reject unknown placeholders");
Check((settingsDefault with { PathTemplate = "/stream\r\nAuthorization: bad" }).Validate() is not null, "Reject request injection");
Check((settingsDefault with { Username = "admin\nHeader" }).Validate() is not null, "Reject username header injection");

foreach (var mode in new[] { "legacy", "qop", "sha256", "basic", "rejected", "missing", "novideo", "oversized", "cancel", "homebase",
    "duplicate-auth", "malformed-status", "negative-length", "truncated-body", "closed" })
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = client.GetStream();
        async Task<string> ReadRequest()
        {
            var text = new StringBuilder();
            var b = new byte[1];
            while (!text.ToString().EndsWith("\r\n\r\n"))
            {
                if (await stream.ReadAsync(b, timeout.Token) == 0)
                {
                    break;
                }

                text.Append((char)b[0]);
            }
            return text.ToString();
        }
        async Task Send(string text)
        {
            // Exercise TCP fragmentation, including inside Content-Length and header terminators.
            var bytes = Encoding.ASCII.GetBytes(text);
            for (var i = 0; i < bytes.Length; i += 7)
            {
                await stream.WriteAsync(bytes.AsMemory(i, Math.Min(7, bytes.Length - i)), timeout.Token);
            }
        }
        var initialRequest = await ReadRequest();
        var malformed = mode switch
        {
            "duplicate-auth" => "RTSP/1.0 401 Unauthorized\r\nWWW-Authenticate: Digest realm=\"camera\", nonce=\"first\", NONCE=\"second\"\r\n\r\n",
            "malformed-status" => "not RTSP\r\n\r\n",
            "negative-length" => "RTSP/1.0 200 OK\r\nContent-Length: -1\r\n\r\n",
            "truncated-body" => "RTSP/1.0 200 OK\r\nContent-Length: 100\r\n\r\nv=0",
            "closed" => "",
            _ => null
        };
        if (malformed is not null)
        {
            await Send(malformed);
            return;
        }
        if (mode == "cancel")
        {
            await Task.Delay(500, timeout.Token);
            return;
        }
        if (mode == "missing")
        {
            await Send("RTSP/1.0 404 Not Found\r\nCSeq: 1\r\n\r\n");
            return;
        }
        if (mode == "oversized")
        {
            await Send("RTSP/1.0 200 OK\r\nContent-Length: 999999999\r\n\r\n");
            return;
        }
        var challenge = mode == "basic" ? "Basic realm=\"Camera\"" : "Digest realm=\"Surveillance Server\", nonce=\"12345678\"" + (mode is "qop" or "sha256" ? ", qop=\"auth,auth-int\", opaque=\"test-opaque\"" : "");
        if (mode == "sha256")
        {
            challenge += ", algorithm=SHA-256";
        }
        var newline = mode is "legacy" or "homebase" ? "\r\r\n" : "\r\n";
        await Send($"RTSP/1.0 401 Unauthorized{newline}CSeq: 1{newline}WWW-Authenticate: {challenge}{newline}{newline}" + (mode == "legacy" ? "\r\n" : ""));
        var request = await ReadRequest();
        var authorization = request.Split("\r\n").Single(x => x.StartsWith("Authorization:"));
        if (mode == "basic")
        {
            if (authorization != "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:p@ssword!")))
            {
                throw new Exception("Basic authentication incorrect");
            }
        }
        else
        {
            var fields = MyRegex().Matches(authorization).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            string Md5(string text) => Convert.ToHexString(mode == "sha256" ? SHA256.HashData(Encoding.UTF8.GetBytes(text)) : MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            var ha1 = Md5("admin:Surveillance Server:p@ssword!");
            var ha2 = Md5($"DESCRIBE:rtsp://127.0.0.1:{port}/ip01/0");
            var expected = mode is "qop" or "sha256" ? Md5($"{ha1}:12345678:00000001:{fields["cnonce"]}:auth:{ha2}") : Md5($"{ha1}:12345678:{ha2}");
            if (fields["response"] != expected)
            {
                throw new Exception("Digest authentication incorrect");
            }

            if (mode == "qop" && fields["opaque"] != "test-opaque")
            {
                throw new Exception("Opaque not preserved");
            }
        }
        if (mode == "homebase")
        {
            // Reproduce HomeBase 2 rejecting an otherwise valid Digest request.
            var rejectedAgent = (initialRequest + request).Contains("User-Agent: SecurityCameraViewer/", StringComparison.OrdinalIgnoreCase);
            await Send($"RTSP/1.0 {(rejectedAgent ? "401 Unauthorized" : "404 Stream Not Found")}{newline}CSeq: 2{newline}{newline}");
            return;
        }
        if (mode == "rejected")
        {
            await Send("RTSP/1.0 401 Unauthorized\r\nCSeq: 2\r\n\r\n");
            return;
        }
        var sdp = "v=0\r\ns=Test camera\r\n" + (mode == "novideo" ? "m=audio 0 RTP/AVP 0\r\n" : "m=video 0 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n");
        await Send($"RTSP/1.0 200 OK\r\nCSeq: 2\r\nContent-Length: {Encoding.ASCII.GetByteCount(sdp)}\r\n\r\n{sdp}");
    });
    using var cancel = new CancellationTokenSource();
    if (mode == "cancel")
    {
        cancel.CancelAfter(100);
    }

    ProbeResult? result = null;
    var cancelled = false;
    try
    {
        result = await RtspProbe.DescribeAsync(new ViewerSettings { Host = "127.0.0.1", RtspPort = port }, "p@ssword!", cancellationToken: cancel.Token);
    }
    catch (OperationCanceledException)
    {
        cancelled = true;
    }
    await server;
    Check(mode switch
    {
        "legacy" or "qop" or "sha256" or "basic" => result is { Success: true, Codec: "H264" },
        "rejected" => result is { Success: false, StatusCode: 401 },
        "missing" or "homebase" => result is { Success: false, StatusCode: 404 },
        "novideo" => result is { Success: false, StatusCode: 200 },
        "oversized" or "duplicate-auth" or "malformed-status" or "negative-length" or "truncated-body" or "closed" => result is { Success: false, StatusCode: 0 },
        "cancel" => cancelled,
        _ => false
    }, $"RTSP {mode} response handling");
}
var migrated = settingsDefault.WithSources();
Check(migrated.Sources.Length == 4 && migrated.Sources[2].MainStream == settingsDefault.StreamUri(3).AbsoluteUri && migrated.CameraId(3) == "camera-3", "Migration preserves recorder streams and integration identity");
var reordered = migrated with { Sources = migrated.Sources.Reverse().ToArray() };
Check(reordered.CameraId(3) == migrated.CameraId(3) && reordered.Channels.First() == 4, "Reordering changes layout without changing source identity");
var disabled = migrated with { Sources = [migrated.Sources[0] with { Enabled = false }, migrated.Sources[1]] };
Check(disabled.Channels.SequenceEqual([2]), "Disabled cameras are excluded from active channels");
var singleStream = migrated.Sources[0] with { SubStream = null };
Check((migrated with { Sources = [singleStream], Substream = true }).StreamUri(1).AbsoluteUri == singleStream.MainStream, "Single-stream cameras have a main-stream fallback");
Check((migrated with { Sources = [singleStream, singleStream] }).Validate() is not null, "Duplicate camera IDs and channels are rejected");
Check((singleStream with { MainStream = "rtsp://user:secret@camera/live" }).Validate() is not null, "Embedded credentials cannot enter source URLs");
var largeRecorder = settingsDefault with
{
    ChannelCount = 32
};
Check(largeRecorder.Validate() is null && largeRecorder.WithSources().Sources.Length == 32 &&
    largeRecorder.WithSources().CameraId(32) == "camera-32", "Legacy recorder configurations support more than sixteen cameras");
var sparseSource = singleStream with
{
    Channel = int.MaxValue,
    Name = "Sparse camera"
};
var sparseSettings = (migrated with
{
    Sources = [sparseSource]
}).Normalize();
Check(sparseSettings.Validate() is null && sparseSettings.ChannelCount == 1 &&
    sparseSettings.CameraName(int.MaxValue) == "Sparse camera", "Normalization does not allocate a name array for sparse internal channels");
var probeXml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(OnvifClient.CreateDiscoveryProbe("urn:uuid:fixture")));
Check(probeXml.Descendants().Single(e => e.Name.LocalName == "Action").Value == "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe" &&
    probeXml.Descendants().Single(e => e.Name.LocalName == "Types").Value == "dn:NetworkVideoTransmitter", "WS-Discovery probe uses the ONVIF type and correct SOAP action");
var exported = CameraDiagnostics.Export(new { password = "private", username = "admin", url = "rtsp://user:secret@camera/live?opaque=private", nested = new[] { "http://camera/device?token=private" } });
Check(!exported.Contains("private") && !exported.Contains("secret") && !exported.Contains("admin") && exported.Contains("REDACTED"), "Nested diagnostic URLs and credentials are redacted");
var visibleExport = CameraDiagnostics.Export(new { password = "private", url = "http://camera/device?token=private" }, true);
Check(visibleExport.Contains("\"password\": \"private\"") && visibleExport.Contains("REDACTED"), "Diagnostic passwords can be included without exposing URL query values");
using (var discoveryFixture = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var discovery = OnvifClient.ProbeInterfaceAsync("127.0.0.1", deadline.Token, (IPEndPoint)discoveryFixture.Client.LocalEndPoint!);
    var datagram = await discoveryFixture.ReceiveAsync(deadline.Token);
    var request = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(datagram.Buffer));
    var messageId = request.Descendants().Single(e => e.Name.LocalName == "MessageID").Value;
    string Reply(string correlation) => $"""
        <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery">
          <s:Header><a:RelatesTo>{correlation}</a:RelatesTo></s:Header><s:Body><d:ProbeMatches><d:ProbeMatch>
            <a:EndpointReference><a:Address>urn:uuid:test-camera</a:Address></a:EndpointReference>
            <d:XAddrs>http://127.0.0.1/onvif/device_service file:///invalid http://user:secret@127.0.0.1/invalid</d:XAddrs>
            <d:Scopes>onvif://www.onvif.org/name/TestCamera</d:Scopes>
          </d:ProbeMatch></d:ProbeMatches></s:Body>
        </s:Envelope>
        """;
    await discoveryFixture.SendAsync(Encoding.UTF8.GetBytes(Reply("unrelated")), datagram.RemoteEndPoint, deadline.Token);
    await discoveryFixture.SendAsync(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY y SYSTEM 'file:///missing'>]><x>&y;</x>"), datagram.RemoteEndPoint, deadline.Token);
    await discoveryFixture.SendAsync(Encoding.UTF8.GetBytes(Reply(messageId)), datagram.RemoteEndPoint, deadline.Token);
    var devices = await discovery;
    Check(devices.Count == 1 && devices[0].Id == "urn:uuid:test-camera" && devices[0].Addresses.Length == 1 && devices[0].Scopes.Length == 1,
        "UDP discovery correlates replies, extracts endpoints/scopes and ignores unrelated packets, unsafe URLs and DTDs");
}
Console.WriteLine($"All {passed} checks passed.");
return 0;
partial class Program
{
    [GeneratedRegex("(\\w+)=(?:\"([^\"]*)\"|([^,\\s]+))")]
    private static partial Regex MyRegex();
}
