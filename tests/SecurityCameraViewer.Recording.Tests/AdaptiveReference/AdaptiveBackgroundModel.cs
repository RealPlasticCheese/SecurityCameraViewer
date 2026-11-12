// Frozen characterization reference from master 35d3b46. Keep the original arithmetic and policy.
using SecurityCameraViewer.Recording;
using SecurityCameraViewer.Core;

namespace AdaptiveReference;

internal readonly record struct IlluminationEstimate(double Shift, double Gain, int IncludedPixels);

/// <summary>Pixel background/noise statistics and colour shadow evidence, independent of motion/zone confirmation.</summary>
internal sealed class AdaptiveBackgroundModel
{
    private float[] _background = [], _variance = [], _color = [];
    private bool[] _foreground = [];
    private readonly int[] _histogram = new int[511];

    public void Reset(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb, AdaptiveMotionSettings options)
    {
        _background = new float[pixels.Length];
        _variance = options.LearnNoise ? new float[pixels.Length] : [];
        _foreground = new bool[pixels.Length];
        _color = options.SuppressShadows ? new float[rgb.Length] : [];
        for (var i = 0; i < pixels.Length; i++)
        {
            _background[i] = pixels[i];
        }
        for (var i = 0; i < _color.Length; i++)
        {
            _color[i] = rgb[i];
        }
    }

    public IlluminationEstimate Estimate(ReadOnlySpan<byte> pixels, bool[] included, bool robust)
    {
        using var measurement = ProcessingMetrics.Measure("motion.illumination");
        double sum = 0;
        var count = 0;
        var stable = 0;
        Array.Clear(_histogram);
        for (var i = 0; i < pixels.Length; i++)
        {
            if (!included[i])
            {
                continue;
            }
            var difference = pixels[i] - _background[i];
            sum += difference;
            count++;
            if (robust && !_foreground[i])
            {
                _histogram[Math.Clamp((int)Math.Round(difference) + 255, 0, 510)]++;
                stable++;
            }
        }
        var mean = sum / Math.Max(1, count);
        if (!robust || stable < Math.Max(16, count / 10))
        {
            return new(mean, 1, count);
        }
        var median = 0;
        var cumulative = 0;
        for (var bucket = 0; bucket < _histogram.Length; bucket++)
        {
            cumulative += _histogram[bucket];
            if (cumulative > stable / 2)
            {
                median = bucket - 255;
                break;
            }
        }
        double x = 0, y = 0, xx = 0, xy = 0;
        var samples = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            if (!included[i] || _foreground[i] || Math.Abs(pixels[i] - _background[i] - median) > 32)
            {
                continue;
            }
            var background = _background[i];
            x += background;
            y += pixels[i];
            xx += background * background;
            xy += background * pixels[i];
            samples++;
        }
        if (samples < 16)
        {
            return new(median, 1, count);
        }
        var variance = xx - x * x / samples;
        var gain = variance / samples > 25 ? Math.Clamp((xy - x * y / samples) / variance, 0.7, 1.3) : 1;
        return new((y - gain * x) / samples, gain, count);
    }

    public int Update(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb, bool[] included, bool[] changed,
        IlluminationEstimate lighting, double threshold, bool warmup, AdaptiveMotionSettings options)
    {
        var shadows = 0;
        using var measurement = ProcessingMetrics.Measure("motion.background");
        var lightingMagnitude = Math.Abs(lighting.Shift);
        var lightingChange = lightingMagnitude > 35;
        for (var i = 0; i < pixels.Length; i++)
        {
            var residual = pixels[i] - _background[i] * lighting.Gain - lighting.Shift;
            var localThreshold = options.LearnNoise ? Math.Max(threshold,
                Math.Min(60, options.NoiseAllowance * Math.Sqrt(_variance[i]))) : threshold;
            var foreground = included[i] && Math.Abs(residual) >= localThreshold;
            if (foreground && options.SuppressShadows && IsShadow(i, pixels[i], rgb, options))
            {
                foreground = false;
                shadows++;
            }
            changed[i] = foreground;
            _foreground[i] = foreground;
            var alpha = warmup || lightingChange ? 0.3f : foreground ? 0.005f : 0.04f;
            if (options.LearnNoise && included[i] && (warmup || !foreground) && lightingMagnitude <= 35)
            {
                _variance[i] += ((float)Math.Min(400, residual * residual) - _variance[i]) * alpha;
            }
            _background[i] += (pixels[i] - _background[i]) * alpha;
            if (options.SuppressShadows)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var colorIndex = i * 3 + channel;
                    _color[colorIndex] += (rgb[colorIndex] - _color[colorIndex]) * alpha;
                }
            }
        }
        return shadows;
    }

    private bool IsShadow(int index, byte gray, ReadOnlySpan<byte> rgb, AdaptiveMotionSettings options)
    {
        var ratio = gray / Math.Max(1, _background[index]);
        if (ratio < options.ShadowMinimumBrightnessRatio || ratio >= 0.98)
        {
            return false;
        }
        var colorIndex = index * 3;
        var sum = rgb[colorIndex] + rgb[colorIndex + 1] + rgb[colorIndex + 2];
        var backgroundSum = _color[colorIndex] + _color[colorIndex + 1] + _color[colorIndex + 2];
        if (sum < 30 || backgroundSum < 30)
        {
            return false;
        }
        for (var channel = 0; channel < 3; channel++)
        {
            if (Math.Abs((double)rgb[colorIndex + channel] / sum - _color[colorIndex + channel] / backgroundSum) > options.ShadowColorTolerance)
            {
                return false;
            }
        }
        return true;
    }
}
