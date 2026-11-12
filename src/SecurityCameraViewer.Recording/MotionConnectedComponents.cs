using System.Runtime.InteropServices;

namespace SecurityCameraViewer.Recording;

internal readonly record struct MotionComponent(int Left, int Top, int Right, int Bottom, int Start, int Count);

/// <summary>Frame-scoped four-connected geometry and pixel membership shared by temporal filtering and observations.</summary>
internal sealed class MotionConnectedComponents
{
    private bool[] _visited = [];
    private int[] _queue = [];
    private readonly List<MotionComponent> _components = [];

    public ReadOnlySpan<MotionComponent> Regions => CollectionsMarshal.AsSpan(_components);

    public ReadOnlySpan<int> Pixels(MotionComponent region) => _queue.AsSpan(region.Start, region.Count);

    public void Find(bool[] pixels, int width, int height)
    {
        if (_visited.Length != pixels.Length)
        {
            _visited = new bool[pixels.Length];
            _queue = new int[pixels.Length];
        }
        Array.Clear(_visited);
        _components.Clear();
        var tail = 0;
        for (var index = 0; index < pixels.Length; index++)
        {
            if (!pixels[index] || _visited[index])
            {
                continue;
            }
            var start = tail;
            var head = tail;
            var left = width;
            var top = height;
            var right = 0;
            var bottom = 0;
            Visit(index, pixels, ref tail);
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
                    Visit(pixel - 1, pixels, ref tail);
                }
                if (x + 1 < width)
                {
                    Visit(pixel + 1, pixels, ref tail);
                }
                if (y > 0)
                {
                    Visit(pixel - width, pixels, ref tail);
                }
                if (y + 1 < height)
                {
                    Visit(pixel + width, pixels, ref tail);
                }
            }
            _components.Add(new(left, top, right, bottom, start, tail - start));
        }
    }

    private void Visit(int pixel, bool[] pixels, ref int tail)
    {
        if (pixels[pixel] && !_visited[pixel])
        {
            _visited[pixel] = true;
            _queue[tail++] = pixel;
        }
    }
}
