using System.Security.Cryptography;
using System.Text;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web.Streaming;

public sealed record StreamingConnection(ViewerSettings Settings, string Password);

public interface IStreamingCameraProvider
{
    StreamingConnection StreamingConnection { get; }
}

public sealed record StreamingMediaInfo(string Codec, int Width, int Height, double FrameRate, int BitrateKbps,
    string? AudioCodec = null, int AudioSampleRate = 8000, int AudioBitrateKbps = 64,
    string MetadataSource = "fallback", string VideoProfile = "Main", string? AudioMedia2Encoding = null);

public sealed record StreamingProfile(CameraSource Camera, bool Substream, StreamingMediaInfo Media)
{
    public string Token => $"{Camera.Id}_{(Substream ? "sub" : "main")}";
    public string SourceToken => $"source_{Camera.Id}";
    public string SourceConfigurationToken => $"video_{Camera.Id}";
    public string EncoderToken => $"encoder_{Token}";
    public string Path => $"cameras/{Camera.Id}/{(Substream ? "sub" : "main")}";
}

/// <summary>Maps active cameras to stable virtual-device identities and immutable media profiles.</summary>
public sealed class StreamingCameraCatalog(IStreamingCameraProvider provider, StreamingSettingsStore settings)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StreamingMediaInfo> _probed = new();
    public StreamingConnection Connection => provider.StreamingConnection;
    public CameraSource[] Cameras => Connection.Settings.WithSources().Sources.Where(c => c.Enabled).ToArray();

    public StreamingProfile[] Profiles(string? cameraId = null) => Profiles(Connection, cameraId);

    public StreamingProfile[] Profiles(StreamingConnection connection, string? cameraId = null)
    {
        return connection.Settings.WithSources().Sources.Where(c => c.Enabled && (cameraId is null || c.Id == cameraId))
            .SelectMany(c => string.IsNullOrEmpty(c.SubStream) ? new[] { Profile(c, false, connection) } : [Profile(c, false, connection), Profile(c, true, connection)])
            .ToArray();
    }

    public StreamingProfile RequireProfile(string? token, string? cameraId)
    {
        return Profiles(cameraId).FirstOrDefault(p => p.Token == token)
            ?? throw new Onvif.OnvifFault("ter:InvalidArgVal", "ter:NoProfile", "Unknown media profile.");
    }

    public Guid DeviceId(string? cameraId) => cameraId is null ? settings.Current.DeviceId :
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{settings.Current.DeviceId:N}/{cameraId}")).AsSpan(0, 16));

    public string DevicePath(string? cameraId) => cameraId is null ? "/onvif" : $"/onvif/cameras/{cameraId}";
    public Uri ServiceUri(string service, string? cameraId) => new(new Uri(settings.Current.OnvifBaseUrl), $"{DevicePath(cameraId)}/{service}");
    public Uri StreamUri(StreamingProfile profile) => new UriBuilder("rtsp", settings.Current.AdvertisedHost, settings.Current.RtspPort, profile.Path).Uri;
    public Uri SnapshotUri(StreamingProfile profile) => ServiceUri($"snapshot/{profile.Token}", profile.Camera.Id);

    public void SetMediaInfo(StreamingProfile profile, StreamingMediaInfo info, StreamingConnection? connection = null) =>
        _probed[MediaKey(profile, connection ?? Connection)] = info;

    private StreamingProfile Profile(CameraSource camera, bool substream, StreamingConnection connection)
    {
        var overrides = settings.Current.Profiles.FirstOrDefault(p => p.CameraId == camera.Id && p.Substream == substream);
        var token = substream ? camera.OnvifSubProfileToken : camera.OnvifProfileToken;
        var saved = camera.Device?.Profiles.FirstOrDefault(p => p.Token == token && p.Width > 0 && p.Height > 0);
        var media = new StreamingMediaInfo(saved?.Codec?.ToUpperInvariant() ?? "H264", saved?.Width ?? (substream ? 640 : 1920),
            saved?.Height ?? (substream ? 360 : 1080), saved?.FrameRate ?? 15, saved?.BitrateKbps ?? (substream ? 512 : 4096),
            MetadataSource: saved is null ? "fallback" : "upstream-onvif");
        var profile = new StreamingProfile(camera, substream, media);
        if (_probed.TryGetValue(MediaKey(profile, connection), out var probed))
        {
            media = probed;
        }

        if (overrides is not null)
        {
            media = media with
            {
                Codec = overrides.Codec, Width = overrides.Width, Height = overrides.Height,
                FrameRate = overrides.FrameRate, BitrateKbps = overrides.BitrateKbps, MetadataSource = "override"
            };
        }

        return profile with { Media = media };
    }

    private static string MediaKey(StreamingProfile profile, StreamingConnection connection) => $"{connection.Settings.ConfigurationId}/{profile.Token}/{(profile.Substream ? profile.Camera.SubStream : profile.Camera.MainStream)}";
}
