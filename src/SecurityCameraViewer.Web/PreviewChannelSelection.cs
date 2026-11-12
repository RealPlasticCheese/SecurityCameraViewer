using System.Globalization;

namespace SecurityCameraViewer.Web;

/// <summary>Opt-in JPEG demand for modern clients, preserving legacy all-camera subscriptions.</summary>
public static class PreviewChannelSelection
{
    public static HashSet<int>? Parse(string? value)
    {
        if (value is null)
        {
            return null;
        }
        var channels = new HashSet<int>();
        if (value.Length == 0)
        {
            return channels;
        }
        foreach (var item in value.Split(','))
        {
            if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var channel) || channel < 1)
            {
                throw new ArgumentException("Preview channels must be positive camera channel numbers.");
            }
            channels.Add(channel);
        }
        return channels;
    }
}
