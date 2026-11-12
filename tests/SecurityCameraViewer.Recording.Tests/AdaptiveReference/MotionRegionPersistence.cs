// Frozen characterization reference from master 35d3b46. Keep the original arithmetic and policy.
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Core;

namespace AdaptiveReference;

/// <summary>Bounds temporal matching to the largest 128 connected regions; never retains borrowed camera frames.</summary>
internal sealed class MotionRegionPersistence
{
    private sealed record Region(int Left, int Top, int Right, int Bottom, int Start, int Count, int Frames);
    private Region[] _previous = [];
    private bool[] _visited = [];
    private int[] _queue = [];
    private double _lastSeconds = double.NegativeInfinity;
    public int[] Progress { get; private set; } = [];

    public void Reset(int length)
    {
        _previous = [];
        _lastSeconds = double.NegativeInfinity;
        _visited = new bool[length];
        _queue = new int[length];
        Progress = new int[length];
    }

    public void Apply(bool[] spatial, bool[] result, int width, int height, double seconds, int requiredFrames, double fps)
    {
        using var measurement = ProcessingMetrics.Measure("motion.persistence");
        Array.Clear(Progress);
        if (requiredFrames == 1)
        {
            Array.Copy(spatial, result, spatial.Length);
            return;
        }
        Array.Clear(result);
        Array.Clear(_visited);
        if (seconds <= _lastSeconds || seconds - _lastSeconds > Math.Max(0.5, 2.5 / fps))
        {
            _previous = [];
        }
        _lastSeconds = seconds;
        var regions = new PriorityQueue<Region, int>();
        var tail = 0;
        for (var index = 0; index < spatial.Length; index++)
        {
            if (!spatial[index] || _visited[index])
            {
                continue;
            }
            var start = tail;
            var head = tail;
            var left = width;
            var top = height;
            var right = 0;
            var bottom = 0;
            Visit(index);
            while (head < tail)
            {
                var pixel = _queue[head++];
                var x = pixel % width;
                var y = pixel / width;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
                if (x > 0)
                {
                    Visit(pixel - 1);
                }
                if (x + 1 < width)
                {
                    Visit(pixel + 1);
                }
                if (y > 0)
                {
                    Visit(pixel - width);
                }
                if (y + 1 < height)
                {
                    Visit(pixel + width);
                }
            }
            var count = tail - start;
            regions.Enqueue(new(left, top, right, bottom, start, count, 1), count);
            if (regions.Count > 128)
            {
                regions.Dequeue();
            }
        }
        var used = new HashSet<Region>();
        var next = new List<Region>();
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
            for (var i = current.Start; i < current.Start + current.Count; i++)
            {
                var pixel = _queue[i];
                Progress[pixel] = frames;
                result[pixel] = frames >= requiredFrames;
            }
        }
        _previous = next.ToArray();

        void Visit(int pixel)
        {
            if (spatial[pixel] && !_visited[pixel])
            {
                _visited[pixel] = true;
                _queue[tail++] = pixel;
            }
        }
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
