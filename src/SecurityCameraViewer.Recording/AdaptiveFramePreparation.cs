using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Mask-aware spatial smoothing. Excluded timestamps and edges cannot bleed into monitored pixels.</summary>
internal sealed class AdaptiveFramePreparation
{
    private byte[] _smoothed = [];
    private int[] _columnSums = [], _columnCounts = [];

    public ReadOnlySpan<byte> Prepare(ReadOnlySpan<byte> pixels, int width, int height, bool[] included, bool smooth)
    {
        if (!smooth)
        {
            return pixels;
        }
        using var measurement = ProcessingMetrics.Measure("motion.smooth");
        if (_smoothed.Length != pixels.Length)
        {
            _smoothed = new byte[pixels.Length];
        }
        if (pixels.IsEmpty)
        {
            return pixels;
        }
        if (_columnSums.Length != width)
        {
            _columnSums = new int[width];
            _columnCounts = new int[width];
        }
        Array.Clear(_columnSums);
        Array.Clear(_columnCounts);
        for (var row = 0; row < Math.Min(height, 2); row++)
        {
            UpdateColumns(pixels, included, row * width, 1);
        }
        for (var y = 0; y < height; y++)
        {
            var sum = _columnSums[0];
            var count = _columnCounts[0];
            if (width > 1)
            {
                sum += _columnSums[1];
                count += _columnCounts[1];
            }
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                _smoothed[index] = count == 0 ? pixels[index] : (byte)(sum / count);
                if (x > 0)
                {
                    sum -= _columnSums[x - 1];
                    count -= _columnCounts[x - 1];
                }
                if (x + 2 < width)
                {
                    sum += _columnSums[x + 2];
                    count += _columnCounts[x + 2];
                }
            }
            if (y > 0)
            {
                UpdateColumns(pixels, included, (y - 1) * width, -1);
            }
            if (y + 2 < height)
            {
                UpdateColumns(pixels, included, (y + 2) * width, 1);
            }
        }
        return _smoothed;
    }

    private void UpdateColumns(ReadOnlySpan<byte> pixels, bool[] included, int offset, int direction)
    {
        for (var column = 0; column < _columnSums.Length; column++)
        {
            if (included[offset + column])
            {
                _columnSums[column] += pixels[offset + column] * direction;
                _columnCounts[column] += direction;
            }
        }
    }
}
