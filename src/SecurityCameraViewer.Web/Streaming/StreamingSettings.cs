using System.Net;
using SecurityCameraViewer.Web.Settings;
using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Web.Streaming;

public sealed record StreamingConfiguration
{
    public bool Enabled { get; init; }
    public bool OnvifEnabled { get; init; } = true;
    public bool DiscoveryEnabled { get; init; } = true;
    public string DiscoveryMode { get; init; } = "individual";
    public string AdvertisedHost { get; init; } = "";
    public string OnvifBaseUrl { get; init; } = "";
    public int RtspPort { get; init; } = 8554;
    public bool UdpEnabled { get; init; } = true;
    public int RtpPort { get; init; } = 8000;
    public int RtcpPort { get; init; } = 8001;
    public int ApiPort { get; init; } = 9997;
    public string MediaMtxExecutable { get; init; } = "mediamtx";
    public string Username { get; init; } = "viewer";
    public Guid DeviceId { get; init; } = Guid.NewGuid();
    public StreamingProfileOverride[] Profiles { get; init; } = [];

    public string? Validate()
    {
        if (RtspPort is < 1024 or > 65535 || ApiPort is < 1024 or > 65535 ||
            RtpPort is < 1024 or > 65535 || RtcpPort is < 1024 or > 65535 ||
            new[] { RtspPort, ApiPort, RtpPort, RtcpPort }.Distinct().Count() != 4)
        {
            return "Choose distinct RTSP, API, RTP and RTCP ports between 1024 and 65535.";
        }

        if (UdpEnabled && (RtpPort % 2 != 0 || RtcpPort != RtpPort + 1))
        {
            return "UDP requires an even RTP port and the next port for RTCP, for example 8000 and 8001.";
        }

        if (AdvertisedHost is null || AdvertisedHost.Length > 253 ||
            AdvertisedHost.Length > 0 && Uri.CheckHostName(AdvertisedHost) == UriHostNameType.Unknown)
        {
            return "Enter the server's LAN IP address or hostname without a scheme or port.";
        }

        if (OnvifBaseUrl is null || OnvifBaseUrl.Length > 2048 || OnvifBaseUrl.Length > 0 &&
            (!Uri.TryCreate(OnvifBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/"))
        {
            return "The ONVIF base URL must be an HTTP(S) server address, for example http://192.168.1.10:5080/.";
        }

        if (Enabled && (AdvertisedHost.Length == 0 || OnvifEnabled && OnvifBaseUrl.Length == 0))
        {
            return "Set an advertised host and ONVIF base URL before enabling the service.";
        }

        if (DiscoveryMode is not ("individual" or "aggregate" or "both") || DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(Username) || Username.Length > 80 || Username == "any" ||
            Username.StartsWith("scv-", StringComparison.Ordinal) || !StreamingAccount.IsSupportedCredential(Username) ||
            string.IsNullOrWhiteSpace(MediaMtxExecutable) || MediaMtxExecutable.Length > 2048 || MediaMtxExecutable.Any(char.IsControl))
        {
            return "Check the discovery mode, username and MediaMTX executable.";
        }

        if (Profiles is null || Profiles.Length > 32 || Profiles.Any(p => p is null || !p.IsValid()) ||
            Profiles.Select(p => (p.CameraId, p.Substream)).Distinct().Count() != Profiles.Length)
        {
            return "Use unique profile overrides with a camera ID, H264/H265/JPEG codec, resolution, frame rate and bitrate.";
        }

        return null;
    }
}

public sealed record StreamingProfileOverride(string CameraId, bool Substream, string Codec, int Width, int Height,
    double FrameRate, int BitrateKbps)
{
    public bool IsValid() => Guid.TryParseExact(CameraId, "N", out _) && Codec is "H264" or "H265" or "JPEG" &&
        Width is >= 1 and <= 16384 && Height is >= 1 and <= 16384 &&
        double.IsFinite(FrameRate) && FrameRate is > 0 and <= 240 && BitrateKbps is > 0 and <= 200000;
}

public sealed record StreamingSettingsRequest(StreamingConfiguration Settings, string? Password);
public sealed record StreamingAccount(string Username, string Password)
{
    // MediaMTX's plaintext credentials use this ASCII alphabet; Digest requires plaintext.
    public static bool IsSupportedCredential(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || "!$()*+.;<=>[]^_-{}@#&".Contains(c));
}

/// <summary>Owns atomic persisted settings and credentials; public responses never contain passwords.</summary>
public sealed class StreamingSettingsStore
{
    private readonly IAppSettingsStore _settingsStore;
    public string FilePath => _settingsStore.FilePath;
    public string RuntimePath => Path.Combine(Path.GetDirectoryName(FilePath)!, "streaming-runtime.json");
    public StreamingConfiguration Current => _settingsStore.Read().Streaming.Settings;

    public (StreamingConfiguration Settings, StreamingAccount Account) Snapshot()
    {
        var current = _settingsStore.Read().Streaming;
        return (current.Settings, new(current.Settings.Username, current.Password));
    }

    public StreamingAccount Account => Snapshot().Account;

    public StreamingSettingsStore(IAppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public void Save(StreamingSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Settings);
        _settingsStore.Update(settings =>
        {
            var previous = settings.Streaming;
            return settings with
            {
                Streaming = new()
                {
                    Settings = request.Settings with { DeviceId = previous.Settings.DeviceId },
                    Password = request.Password ?? previous.Password
                }
            };
        });
    }
}
