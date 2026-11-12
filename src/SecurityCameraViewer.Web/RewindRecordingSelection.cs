using SecurityCameraViewer.Core;
using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

public sealed record SaveRewindRecordingRequest(int Generation, long Start, long End, int[] Channels)
{
    public void Validate()
    {
        if (Start == End || Math.Min(Start, End) < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
            Math.Max(Start, End) > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            Channels is null or { Length: 0 } || Channels.Any(channel => channel < 1) ||
            Channels.Distinct().Count() != Channels.Length)
        {
            throw new ArgumentException("Choose two different playback times and the cameras to save.");
        }
    }
}

public sealed record RewindRecordingCamera(int Channel, string CameraId, string Name, RecordingClip[] Clips);
public sealed record RewindRecordingSelection(string ConfigurationId, RewindRecordingCamera[] Cameras)
{
    public static RewindRecordingSelection Capture(SaveRewindRecordingRequest request, ViewerSettings connection,
        Func<int, LiveRewindBuffer?> buffer)
    {
        request.Validate();
        var start = Math.Min(request.Start, request.End);
        var end = Math.Max(request.Start, request.End);
        var cameras = new List<RewindRecordingCamera>();
        foreach (var channel in request.Channels)
        {
            if (!connection.Channels.Contains(channel))
            {
                throw new ArgumentException("Choose cameras in the active configuration.");
            }
            var clips = buffer(channel)?.Select(start, end) ?? [];
            if (clips.Length == 0)
            {
                continue;
            }
            cameras.Add(new(channel, connection.CameraId(channel), connection.CameraName(channel), clips));
        }
        if (cameras.Count == 0)
        {
            throw new InvalidOperationException("No buffered footage is available from the selected cameras in this interval. Choose an interval with footage.");
        }
        return new(connection.ConfigurationId, [.. cameras]);
    }
}
