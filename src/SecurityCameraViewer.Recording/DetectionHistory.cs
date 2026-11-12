namespace SecurityCameraViewer.Recording;

/// <summary>Metadata retention budgets, independent of recording and inference resources.</summary>
public sealed record DetectionHistoryOptions
{
    public double Seconds { get; init; } = 30;
    public int MaximumSamplesPerCamera { get; init; } = 600;

    public void Validate()
    {
        if (!double.IsFinite(Seconds) || Seconds <= 0 || MaximumSamplesPerCamera <= 0)
        {
            throw new ArgumentException("Detection history duration and sample budget must be positive.");
        }
    }
}

public sealed record DetectionFrame(DateTimeOffset CapturedAt, DateTimeOffset PublishedAt, DetectionObservation[] Detections);
public sealed record DetectionHistorySnapshot(string Id, DetectionFrame[] Frames);

/// <summary>Retains sampled results, including empty frames, for delayed live presentation. No image pixels are retained.</summary>
public sealed class DetectionHistory
{
    private readonly object _sync = new();
    private readonly Queue<DetectionFrame> _frames = new();
    private readonly DetectionHistoryOptions _options;
    private string _id = Guid.NewGuid().ToString("N");
    private DateTimeOffset? _lastCapturedAt;

    public DetectionHistory(DetectionHistoryOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    public void Add(DateTimeOffset capturedAt, DetectionObservation[] detections, DateTimeOffset publishedAt)
    {
        lock (_sync)
        {
            if (_lastCapturedAt > capturedAt)
            {
                ClearCore();
            }
            _lastCapturedAt = capturedAt;
            _frames.Enqueue(new(capturedAt, publishedAt, [.. detections]));
            Prune(publishedAt);
        }
    }

    public DetectionHistorySnapshot Snapshot(DateTimeOffset now, DateTimeOffset? since = null)
    {
        lock (_sync)
        {
            Prune(now);
            return new(_id, _frames.Where(frame => frame.PublishedAt <= now &&
                (since is null || since > now || frame.PublishedAt >= since)).ToArray());
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            ClearCore();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_frames.TryPeek(out var first) && (_frames.Count > _options.MaximumSamplesPerCamera ||
            (now - first.PublishedAt).TotalSeconds > _options.Seconds))
        {
            _frames.Dequeue();
        }
    }

    private void ClearCore()
    {
        _frames.Clear();
        _lastCapturedAt = null;
        _id = Guid.NewGuid().ToString("N");
    }
}
