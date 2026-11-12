using System.Text.Json;

namespace SecurityCameraViewer.Web.Streaming;

/// <summary>JSON is a YAML subset accepted by MediaMTX. Never expose this private runtime document.</summary>
public static class MediaMtxConfiguration
{
    public static string Create(StreamingConfiguration settings, StreamingAccount account, StreamingCameraCatalog catalog,
        StreamingAccount internalAccount, StreamingConnection? connection = null)
    {
        connection ??= catalog.Connection;
        var paths = catalog.Profiles(connection).ToDictionary(p => p.Path, p => new
        {
            source = SourceUri(p, connection),
            rtspTransport = "tcp",
            sourceOnDemand = true,
            sourceOnDemandStartTimeout = "15s",
            sourceOnDemandCloseAfter = "10s",
            maxReaders = 64
        });
        object User(StreamingAccount user, string[] ips, params string[] actions) => new
        {
            user = user.Username, pass = user.Password, ips,
            permissions = actions.Select(action => new { action }).ToArray()
        };
        return JsonSerializer.Serialize(new
        {
            logLevel = "warn", logDestinations = new[] { "stdout" },
            rtsp = true, rtspAddress = $":{settings.RtspPort}", rtspAuthMethods = new[] { "basic", "digest" },
            rtspTransports = settings.UdpEnabled ? new[] { "tcp", "udp" } : ["tcp"],
            rtpAddress = $":{settings.RtpPort}", rtcpAddress = $":{settings.RtcpPort}",
            api = true, apiAddress = $"127.0.0.1:{settings.ApiPort}",
            authMethod = "internal",
            authInternalUsers = new[] { User(account, [], "read"), User(internalAccount, ["127.0.0.1", "::1"], "read", "api") },
            rtmp = false, hls = false, webrtc = false, srt = false, moq = false, playback = false, metrics = false, pprof = false,
            paths
        });
    }

    private static string SourceUri(StreamingProfile profile, StreamingConnection connection)
    {
        var source = profile.Substream ? profile.Camera.SubStream! : profile.Camera.MainStream;
        var user = connection.Settings.CameraUsername(profile.Camera.Channel);
        var password = connection.Settings.CameraPassword(profile.Camera.Channel, connection.Password);
        return new UriBuilder(source) { UserName = user, Password = password }.Uri.AbsoluteUri;
    }
}
