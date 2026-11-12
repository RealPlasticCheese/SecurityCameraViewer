namespace SecurityCameraViewer.Recording;

public sealed record VideoFragment(byte[] Bytes, double DurationSeconds, double StartSeconds = 0, bool StartsWithKeyframe = true)
{
    // Timestamp before any hub, WebSocket or client buffering. The end of the source interval
    // corresponds to fragment production; StartSeconds retains the MP4 media timeline.
    public DateTimeOffset ProducedAt { get; init; } = DateTimeOffset.UtcNow;
    public double? PresentationEndSeconds { get; init; }
}

/// <summary>Owns only compressed keyframe fragments. Never uses files. Callers serialize access.</summary>
public sealed class VideoBuffer(double seconds, long maximumBytes)
{
    private readonly Queue<VideoFragment> _fragments = new();
    public long Bytes
    {
        get; private set;
    }
    public double Seconds
    {
        get; private set;
    }
    public bool Limited
    {
        get; private set;
    }
    public void Add(VideoFragment fragment)
    {
        if (fragment.Bytes.LongLength > maximumBytes)
        {
            throw new InvalidDataException("A camera keyframe interval exceeds the RAM buffer limit.");
        }

        _fragments.Enqueue(fragment);
        Bytes += fragment.Bytes.Length;
        Seconds += fragment.DurationSeconds;
        var evictedForSize = false;
        while (_fragments.Count > 1 && (Seconds - _fragments.Peek().DurationSeconds >= seconds || Bytes > maximumBytes))
        {
            evictedForSize |= Bytes > maximumBytes;
            var old = _fragments.Dequeue();
            Bytes -= old.Bytes.Length;
            Seconds -= old.DurationSeconds;
        }
        Limited = evictedForSize && Seconds + 0.1 < seconds;
    }
    public VideoFragment[] Snapshot() => [.. _fragments];
}
