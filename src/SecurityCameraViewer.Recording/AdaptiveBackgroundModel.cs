using System.Runtime.CompilerServices;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

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
        if (!robust)
        {
            var total = AdaptivePixelKernels.MeanSum(pixels, _background, included, out var includedPixels);
            return new(total / Math.Max(1, includedPixels), 1, includedPixels);
        }
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
            if (!_foreground[i])
            {
                _histogram[Math.Clamp((int)Math.Round(difference) + 255, 0, 510)]++;
                stable++;
            }
        }
        var mean = sum / Math.Max(1, count);
        if (stable < Math.Max(16, count / 10))
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
        var learnNoise = options.LearnNoise;
        var suppressShadows = options.SuppressShadows;
        var noiseAllowance = options.NoiseAllowance;
        var noiseAllowanceSquared = noiseAllowance * noiseAllowance;
        if (!learnNoise && suppressShadows)
        {
            return UpdateShadows(pixels, rgb, included, changed, lighting, threshold, warmup || lightingChange, options);
        }
        var plain = !learnNoise && !suppressShadows;
        var start = plain ? AdaptivePixelKernels.Update(pixels, _background, included, changed, _foreground,
            lighting, threshold, warmup || lightingChange) : 0;
        for (var i = start; i < pixels.Length; i++)
        {
            var residual = pixels[i] - _background[i] * lighting.Gain - lighting.Shift;
            var foreground = included[i] && (learnNoise
                ? ExceedsNoiseThreshold(residual, threshold, _variance[i], noiseAllowance, noiseAllowanceSquared)
                : Math.Abs(residual) >= threshold);
            if (foreground && suppressShadows && IsShadow(i, pixels[i], rgb, options))
            {
                foreground = false;
                shadows++;
            }
            changed[i] = foreground;
            _foreground[i] = foreground;
            var alpha = warmup || lightingChange ? 0.3f : foreground ? 0.005f : 0.04f;
            if (learnNoise && included[i] && (warmup || !foreground) && lightingMagnitude <= 35)
            {
                _variance[i] += ((float)Math.Min(400, residual * residual) - _variance[i]) * alpha;
            }
            _background[i] += (pixels[i] - _background[i]) * alpha;
            if (suppressShadows)
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

    private int UpdateShadows(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> rgb, bool[] included, bool[] changed,
        IlluminationEstimate lighting, double threshold, bool recovering, AdaptiveMotionSettings options)
    {
        var shadows = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var residual = pixels[i] - _background[i] * lighting.Gain - lighting.Shift;
            var foreground = included[i] && Math.Abs(residual) >= threshold;
            if (foreground && IsShadow(i, pixels[i], rgb, options))
            {
                foreground = false;
                shadows++;
            }
            changed[i] = foreground;
            _foreground[i] = foreground;
            var alpha = recovering ? .3f : foreground ? .005f : .04f;
            _background[i] += (pixels[i] - _background[i]) * alpha;
            for (var channel = 0; channel < 3; channel++)
            {
                var index = i * 3 + channel;
                _color[index] += (rgb[index] - _color[index]) * alpha;
            }
        }
        return shadows;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ExceedsNoiseThreshold(double residual, double threshold, float variance,
        double noiseAllowance, double noiseAllowanceSquared)
    {
        var magnitude = Math.Abs(residual);
        if (magnitude < threshold)
        {
            return false;
        }
        if (magnitude >= 60)
        {
            return true;
        }
        var squared = residual * residual;
        var noiseSquared = noiseAllowanceSquared * variance;
        // Squaring changes rounding at the boundary. Retain the original sqrt comparison there.
        var roundingMargin = 16 * 2.2204460492503131e-16 * Math.Max(squared, noiseSquared);
        if (Math.Abs(squared - noiseSquared) > roundingMargin)
        {
            return squared > noiseSquared;
        }
        return magnitude >= Math.Max(threshold, Math.Min(60, noiseAllowance * Math.Sqrt(variance)));
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
