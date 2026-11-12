using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Bounds temporal matching to the largest 128 connected regions; never retains borrowed camera frames.</summary>
internal sealed class MotionRegionPersistence
{
    private sealed record Region(int Left, int Top, int Right, int Bottom, int Start, int Count, int Frames);
    private Region[] _previous = [];
    public MotionConnectedComponents Components { get; } = new();
    private double _lastSeconds = double.NegativeInfinity;
    public int[] Progress { get; private set; } = [];

    public void Reset(int length)
    {
        _previous = [];
        _lastSeconds = double.NegativeInfinity;
        Progress = new int[length];
    }

    public int Apply(bool[] spatial, bool[] result, int width, int height, double seconds, int requiredFrames, double fps)
    {
        using var measurement = ProcessingMetrics.Measure("motion.persistence");
        Array.Clear(Progress);
        if (requiredFrames == 1)
        {
            var count = 0;
            for (var index = 0; index < spatial.Length; index++)
            {
                result[index] = spatial[index];
                if (spatial[index])
                {
                    count++;
                }
            }
            return count;
        }
        Array.Clear(result);
        if (seconds <= _lastSeconds || seconds - _lastSeconds > Math.Max(0.5, 2.5 / fps))
        {
            _previous = [];
        }
        _lastSeconds = seconds;
        Components.Find(spatial, width, height);
        var regions = new PriorityQueue<Region, int>();
        foreach (var component in Components.Regions)
        {
            regions.Enqueue(new(component.Left, component.Top, component.Right, component.Bottom, component.Start, component.Count, 1), component.Count);
            if (regions.Count > 128)
            {
                regions.Dequeue();
            }
        }
        var used = new HashSet<Region>();
        var next = new List<Region>();
        var changed = 0;
        foreach (var current in regions.UnorderedItems.OrderByDescending(item => item.Priority).Select(item => item.Element))
        {
            var match = _previous.Where(previous => !used.Contains(previous)).Select(previous => (Region: previous, Score: Overlap(current, previous)))
                .Where(item => item.Score >= 0.2).OrderByDescending(item => item.Score).FirstOrDefault();
            var frames = 1;
            if (match.Region is not null)
            {
                used.Add(match.Region);
                frames = Math.Min(requiredFrames, match.Region.Frames + 1);
            }
            next.Add(current with
            {
                Frames = frames
            });
            var confirmed = frames >= requiredFrames;
            if (confirmed)
            {
                changed += current.Count;
            }
            foreach (var pixel in Components.Pixels(new(current.Left, current.Top, current.Right, current.Bottom, current.Start, current.Count)))
            {
                Progress[pixel] = frames;
                result[pixel] = confirmed;
            }
        }
        _previous = next.ToArray();
        return changed;
    }

    private static double Overlap(Region current, Region previous)
    {
        var width = Math.Max(0, Math.Min(current.Right, previous.Right + 2) - Math.Max(current.Left, previous.Left - 2) + 1);
        var height = Math.Max(0, Math.Min(current.Bottom, previous.Bottom + 2) - Math.Max(current.Top, previous.Top - 2) + 1);
        var area = Math.Min((current.Right - current.Left + 1) * (current.Bottom - current.Top + 1),
            (previous.Right - previous.Left + 1) * (previous.Bottom - previous.Top + 1));
        return (double)width * height / Math.Max(1, area);
    }
}
