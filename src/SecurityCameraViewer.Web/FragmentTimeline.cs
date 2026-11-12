using SecurityCameraViewer.Recording;

namespace SecurityCameraViewer.Web;

/// <summary>Maps an encoded stream to the server clock before delivery to listeners.
/// A short production window separates sustained source pacing from isolated flush jitter.</summary>
public sealed class FragmentTimeline
{
    public const double MaximumRelativeIntervalVariation = 0.1;
    private readonly Queue<double> _intervals = new();
    private Sample? _previous;
    public bool Calibrated { get; private set; }

    public FragmentTiming Map(VideoFragment fragment)
    {
        var end = fragment.PresentationEndSeconds ?? fragment.StartSeconds + fragment.DurationSeconds;
        if (!double.IsFinite(fragment.StartSeconds) || fragment.StartSeconds < 0 ||
            !double.IsFinite(fragment.DurationSeconds) || fragment.DurationSeconds is <= 0 or > 30 ||
            !double.IsFinite(end) || end <= fragment.StartSeconds)
        {
            throw new InvalidDataException("Invalid live fragment timing.");
        }
        var produced = fragment.ProducedAt.ToUnixTimeMilliseconds();
        var previous = _previous;
        if (previous is not null && (fragment.StartSeconds < previous.Start || end <= previous.End || produced < previous.Produced))
        {
            throw new InvalidDataException("Live fragment timestamps went backwards.");
        }
        if (previous is not null)
        {
            _intervals.Enqueue((produced - previous.Produced) / ((end - previous.End) * 1000));
            if (_intervals.Count > 3)
            {
                _intervals.Dequeue();
            }
        }
        var sorted = _intervals.Order().ToArray();
        var median = sorted.Length == 3 ? sorted[1] : 0;
        var consistent = median > 0 && double.IsFinite(median) && sorted[2] - sorted[0] <= median * MaximumRelativeIntervalVariation;
        var rate = consistent ? median : previous?.Timing.ClockRate ?? 1;
        Calibrated |= consistent;
        var origin = produced - end * rate * 1000;
        // Using neighboring production times prevents a single delayed flush from advancing footage.
        // A sustained pause establishes a new mapping, without rewriting older history.
        var calibrated = previous is null ? origin : Math.Min(previous.Produced - previous.End * rate * 1000, origin);
        if (previous is not null && rate == previous.Timing.ClockRate &&
            produced - previous.Produced < (end - previous.End) * previous.Timing.ClockRate * 1000)
        {
            // A short catch-up interval is queued delivery, not a new capture-time origin.
            calibrated = Math.Min(calibrated, previous.Timing.OriginMilliseconds);
        }
        var timing = new FragmentTiming(calibrated, rate);
        _previous = new(fragment.StartSeconds, end, produced, timing);
        return timing;
    }

    private sealed record Sample(double Start, double End, long Produced, FragmentTiming Timing);
}

public sealed record FragmentTiming(double OriginMilliseconds, double ClockRate)
{
    public long Time(double mediaSeconds) => checked((long)Math.Round(OriginMilliseconds + mediaSeconds * ClockRate * 1000));
}
