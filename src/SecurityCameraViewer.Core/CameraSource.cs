using System.Globalization;
using System.Text.Json.Serialization;

namespace SecurityCameraViewer.Core;

/// <summary>A logical camera, independent of the recorder or ONVIF service which supplied its URLs.</summary>
public sealed record CameraSource
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    // Stable internal key for recording history, filters and legacy integrations.
    // Display numbers are derived from collection order by the browser.
    public int Channel
    {
        get; init;
    }
    public string Name { get; init; } = "Camera";
    public string MainStream { get; init; } = "";
    public string MainStreamAspectRatio { get; init; } = "auto";
    public string? SubStream
    {
        get; init;
    }
    public string SubStreamAspectRatio { get; init; } = "auto";
    public string Username { get; init; } = "";
    // A saved preference; browser capability checks resolve Automatic per viewing device.
    public string PlaybackMethod { get; init; } = "automatic";
    public bool MotionActivated
    {
        get; init;
    }
    public bool Enabled
    {
        get; init;
    } = true;
    public decimal PollIntervalSeconds
    {
        get; init;
    } = 1;
    public bool UseDefaultPassword
    {
        get; init;
    }
    public string? OnvifAddress
    {
        get; init;
    }
    public string? OnvifProfileToken
    {
        get; init;
    }
    public string? OnvifSubProfileToken
    {
        get; init;
    }
    public string? VideoSourceToken
    {
        get; init;
    }
    public string Origin { get; init; } = "RTSP";
    public OnvifDeviceReport? Device
    {
        get; init;
    }

    public string? Validate()
    {
        if (!ValidAspectRatio(MainStreamAspectRatio) || !ValidAspectRatio(SubStreamAspectRatio))
        {
            return "Choose Auto or an aspect ratio with positive width:height numbers, such as 16:9 or 1.85:1.";
        }
        if (PlaybackMethod is not ("automatic" or "native" or "browser" or "server"))
        {
            return "Choose automatic, native video, browser decoding or server JPEG previews.";
        }
        if (PollIntervalSeconds is < 0.1m or > 3600m || PollIntervalSeconds != decimal.Round(PollIntervalSeconds, 1))
        {
            return "Camera poll interval must be 0.1–3600 seconds, in increments of 0.1 second.";
        }
        if (!Guid.TryParseExact(Id, "N", out _) || Channel < 1)
        {
            return "Each camera needs a unique ID and a positive internal channel.";
        }

        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || !ValidRtsp(MainStream) ||
            !string.IsNullOrEmpty(SubStream) && !ValidRtsp(SubStream))
        {
            return "Enter a camera name (up to 80 characters) and an RTSP URL without embedded credentials or fragments.";
        }

        if (Username is null || Username.Length > 256 || Username.IndexOfAny(['\r', '\n', '"', ':']) >= 0)
        {
            return "The camera username contains unsupported characters.";
        }

        if (!string.IsNullOrEmpty(OnvifAddress) && !OnvifClient.IsServiceAddress(OnvifAddress))
        {
            return "The ONVIF address must be an HTTP(S) URL without credentials or a fragment.";
        }

        return null;
    }

    private static bool ValidAspectRatio(string? value)
    {
        if (value == "auto")
        {
            return true;
        }
        var parts = value?.Split(':');
        return parts is { Length: 2 } &&
            double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var width) &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var height) &&
            width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height) &&
            double.IsFinite(width / height) && width / height > 0;
    }

    public static bool ValidRtsp(string? value)
    {
        return value is { Length: <= 2048 } && !value.Any(char.IsControl) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "rtsp" &&
            uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    }
}

public sealed record CameraCredentials(string Password = "", string? OnvifUsername = null, string? OnvifPassword = null);

/// <summary>A saved collection. Passwords are managed separately by the host.</summary>
public sealed record ConfigurationCatalog
{
    public int Version { get; init; } = 1;
    public string ActiveId { get; init; } = "default";
    public ViewerSettings[] Configurations { get; init; } = [];

    public string? Validate()
    {
        if (Version != 1 || Configurations is null || Configurations.Length is < 1 or > 32 ||
            Configurations.Any(c => c is null || c.Validate() is not null) ||
            Configurations.Select(c => c.ConfigurationId).Distinct().Count() != Configurations.Length ||
            !Configurations.Any(c => c.ConfigurationId == ActiveId))
        {
            return "Choose an active configuration and up to 32 valid configurations with unique IDs.";
        }

        return null;
    }
}
