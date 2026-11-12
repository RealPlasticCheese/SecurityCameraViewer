using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Web;

/// <summary>One availability gate per configured camera, shared by viewing and recording.</summary>
public sealed class BatteryCameraRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<(string, string), Entry> _entries = [];
    private sealed record Entry(CameraSource Source, string Password, BatteryCameraAvailability Availability);

    public BatteryCameraAvailability? Get(ViewerSettings settings, string password, int channel)
    {
        var source = settings.Sources.FirstOrDefault(c => c.Channel == channel);
        if (source?.MotionActivated != true)
        {
            return null;
        }

        var secret = settings.CameraPassword(channel, password);
        lock (_sync)
        {
            var key = (settings.ConfigurationId, source.Id);
            if (_entries.TryGetValue(key, out var entry) && entry.Source.MainStream == source.MainStream &&
                entry.Source.Username == source.Username && entry.Source.PollIntervalSeconds == source.PollIntervalSeconds && entry.Password == secret)
            {
                return entry.Availability;
            }

            var availability = new BatteryCameraAvailability(async token =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    return await RtspProbe.DescribeAsync(settings with
                    {
                        Substream = false
                    }, password, channel, timeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new(false, 0, "HomeBase/camera did not respond to the RTSP check.");
                }
            }, TimeSpan.FromSeconds((double)source.PollIntervalSeconds));
            // Configuration switches should not retain credentials for inactive collections indefinitely.
            foreach (var old in _entries.Keys.Where(k => k.Item1 != settings.ConfigurationId || !settings.Sources.Any(c => c.Id == k.Item2)).ToArray())
            {
                _entries.Remove(old);
            }

            _entries[key] = new(source, secret, availability);
            return availability;
        }
    }
}
