using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Core;

public sealed record ViewerSettings
{
    public string ConfigurationId { get; init; } = "default";
    public string ConfigurationName { get; init; } = "My cameras";
    public CameraSource[] Sources { get; init; } = [];
    [JsonIgnore]
    public IReadOnlyDictionary<string, CameraCredentials> Credentials { get; init; } = new Dictionary<string, CameraCredentials>();
    [JsonIgnore]
    public IEnumerable<int> Channels => Sources.Length == 0 ? Enumerable.Range(1, ChannelCount) : Sources.Where(c => c.Enabled).Select(c => c.Channel);

    public string Host { get; init; } = "192.0.2.10";
    public int RtspPort { get; init; } = 554;
    public string Username { get; init; } = "admin";
    public int ChannelCount { get; init; } = 4;
    public string PathTemplate { get; init; } = "/ip{channel:00}/{stream}";
    public bool Substream
    {
        get; init;
    }
    public int CacheMilliseconds { get; init; } = 500;
    public bool AutoConnect { get; init; } = true;
    public string RetryDelaysSeconds { get; init; } = CameraRetrySchedule.DefaultDelaysSeconds;
    public int VideoTimeoutSeconds
    {
        get; init;
    }
    // Legacy recorder names only. Explicit sources own their names; their order owns display numbering.
    public string[] CameraNames { get; init; } = ["Camera 1", "Camera 2", "Camera 3", "Camera 4"];

    public string? Validate()
    {
        if (!CameraRetrySchedule.TryParse(RetryDelaysSeconds, out _))
        {
            return CameraRetrySchedule.ValidationMessage;
        }

        if (VideoTimeoutSeconds < 0)
        {
            return "Video timeout must be zero or a positive number of seconds.";
        }

        if ((ConfigurationId != "default" && !Guid.TryParseExact(ConfigurationId, "N", out _)) ||
            string.IsNullOrWhiteSpace(ConfigurationName) || ConfigurationName.Length > 80)
        {
            return "Enter a configuration name (up to 80 characters) and a valid configuration ID.";
        }

        if (Sources is null || Sources.Any(c => c is null) ||
            Sources.Select(c => c.Id).Distinct().Count() != Sources.Length ||
            Sources.Select(c => c.Channel).Distinct().Count() != Sources.Length)
        {
            return "Use cameras with unique IDs and internal channels.";
        }

        if (Sources.Length > 0)
        {
            return Sources.Select(c => c.Validate()).FirstOrDefault(error => error is not null) ??
                (CacheMilliseconds is < 100 or > 5000 ? "Buffering must be between 100 and 5000 milliseconds." : null);
        }

        if (string.IsNullOrWhiteSpace(Host) || Uri.CheckHostName(Host) == UriHostNameType.Unknown)
        {
            return "Enter an IP address or hostname without a scheme or port.";
        }

        if (RtspPort is < 1 or > 65535)
        {
            return "The RTSP port must be between 1 and 65535.";
        }

        if (ChannelCount < 1)
        {
            return "Choose at least one camera.";
        }

        if (CacheMilliseconds is < 100 or > 5000)
        {
            return "Buffering must be between 100 and 5000 milliseconds.";
        }

        if (Username is null || Username.IndexOfAny(['\r', '\n', '"', ':']) >= 0)
        {
            return "The username contains unsupported characters.";
        }

        if (CameraNames is null)
        {
            return "Camera names could not be read.";
        }

        if (string.IsNullOrWhiteSpace(PathTemplate) || !PathTemplate.StartsWith('/') ||
            PathTemplate.Any(char.IsWhiteSpace) || PathTemplate.Contains('@') || PathTemplate.Contains('#'))
        {
            return "Enter a stream path beginning with /, without spaces, credentials or a fragment.";
        }

        var expanded = ExpandPath(1);
        if (expanded.Contains('{') || expanded.Contains('}'))
        {
            return "Supported placeholders: {channel}, {channel:00}, {channel0}, {stream}.";
        }

        return null;
    }

    public string ExpandPath(int channel) => PathTemplate
        .Replace("{channel:00}", channel.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{channel}", channel.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{channel0}", (channel - 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{stream}", Substream ? "1" : "0");

    public Uri StreamUri(int channel) => Sources.FirstOrDefault(c => c.Channel == channel) is { } source
        ? new Uri(Substream && !string.IsNullOrEmpty(source.SubStream) ? source.SubStream : source.MainStream)
        : new UriBuilder("rtsp", Host, RtspPort)
        {
            Path = ExpandPath(channel).Split('?', 2)[0],
            Query = ExpandPath(channel).Contains('?') ? ExpandPath(channel).Split('?', 2)[1] : ""
        }.Uri;

    public string CameraName(int channel) => Sources.FirstOrDefault(c => c.Channel == channel)?.Name ?? (CameraNames.Length >= channel &&
        !string.IsNullOrWhiteSpace(CameraNames[channel - 1]) ? CameraNames[channel - 1] : $"Camera {channel}");

    public string CameraUsername(int channel) => Sources.FirstOrDefault(c => c.Channel == channel)?.Username ?? Username;

    public string CameraId(int channel)
    {
        var source = Sources.FirstOrDefault(c => c.Channel == channel);
        return source is null || ConfigurationId == "default" && source.Id == channel.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)
            ? $"camera-{channel}" : $"camera-{source.Id}";
    }

    public string CameraPassword(int channel, string fallback)
    {
        var source = Sources.FirstOrDefault(c => c.Channel == channel);
        return source is null || source.UseDefaultPassword ? fallback : Credentials.GetValueOrDefault(source.Id)?.Password ?? "";
    }

    public ViewerSettings Normalize()
    {
        return Sources.Length == 0 ? this : this with
        {
            ChannelCount = Sources.Length
        };
    }

    public ViewerSettings WithSources()
    {
        if (Sources.Length > 0)
        {
            return Normalize();
        }

        return this with
        {
            Sources = Channels.Select(channel => new CameraSource
            {
                Id = channel.ToString("x32", System.Globalization.CultureInfo.InvariantCulture),
                Channel = channel,
                Name = CameraName(channel),
                MainStream = (this with { Substream = false }).StreamUri(channel).AbsoluteUri,
                SubStream = PathTemplate.Contains("{stream}", StringComparison.Ordinal)
                    ? (this with
                    {
                        Substream = true
                    }).StreamUri(channel).AbsoluteUri : null,
                Username = Username,
                UseDefaultPassword = true,
                Origin = "Recorder template"
            }).ToArray()
        };
    }
}
