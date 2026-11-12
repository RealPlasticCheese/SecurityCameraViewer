using System.Numerics;
using System.Runtime.InteropServices;

namespace SecurityCameraViewer.Recording;

/// <summary>Optional SIMD for the plain background model. Scalar tails preserve the original arithmetic.</summary>
internal static class AdaptivePixelKernels
{
    public static int Update(ReadOnlySpan<byte> pixels, float[] background, bool[] included, bool[] changed,
        bool[] foreground, IlluminationEstimate lighting, double threshold, bool recovering)
    {
        var offset = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var includedBytes = MemoryMarshal.AsBytes(included.AsSpan());
            var changedBytes = MemoryMarshal.AsBytes(changed.AsSpan());
            var foregroundBytes = MemoryMarshal.AsBytes(foreground.AsSpan());
            for (; offset <= pixels.Length - Vector<byte>.Count; offset += Vector<byte>.Count)
            {
                Widen(new Vector<byte>(pixels.Slice(offset)), out var p0, out var p1, out var p2, out var p3);
                Widen(new Vector<byte>(includedBytes.Slice(offset)), out var m0, out var m1, out var m2, out var m3);
                var f0 = UpdateBlock(p0, m0, background, offset, lighting, threshold, recovering);
                var f1 = UpdateBlock(p1, m1, background, offset + Vector<float>.Count, lighting, threshold, recovering);
                var f2 = UpdateBlock(p2, m2, background, offset + Vector<float>.Count * 2, lighting, threshold, recovering);
                var f3 = UpdateBlock(p3, m3, background, offset + Vector<float>.Count * 3, lighting, threshold, recovering);
                var packed = Vector.AsVectorByte(Vector.Narrow(Vector.Narrow(f0, f1), Vector.Narrow(f2, f3))) & new Vector<byte>(1);
                packed.CopyTo(changedBytes.Slice(offset));
                packed.CopyTo(foregroundBytes.Slice(offset));
            }
        }
        return offset;
    }

    private static Vector<int> UpdateBlock(Vector<float> pixels, Vector<float> included, float[] background,
        int offset, IlluminationEstimate lighting, double threshold, bool recovering)
    {
        var previous = new Vector<float>(background, offset);
        Vector.Widen(previous, out var lowBackground, out var highBackground);
        Vector.Widen(pixels, out var lowPixels, out var highPixels);
        var gain = new Vector<double>(lighting.Gain);
        var shift = new Vector<double>(lighting.Shift);
        var limit = new Vector<double>(threshold);
        var low = Vector.GreaterThanOrEqual(Vector.Abs(lowPixels - lowBackground * gain - shift), limit);
        var high = Vector.GreaterThanOrEqual(Vector.Abs(highPixels - highBackground * gain - shift), limit);
        var moving = Vector.Narrow(Vector.AsVectorInt64(low), Vector.AsVectorInt64(high)) &
            Vector.GreaterThan(included, Vector<float>.Zero);
        var alpha = recovering ? new Vector<float>(.3f) :
            Vector.ConditionalSelect(moving, new Vector<float>(.005f), new Vector<float>(.04f));
        (previous + (pixels - previous) * alpha).CopyTo(background, offset);
        return moving;
    }

    public static double MeanSum(ReadOnlySpan<byte> pixels, float[] background, bool[] included, out int count)
    {
        double sum = 0;
        count = 0;
        var offset = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; offset <= pixels.Length - Vector<byte>.Count; offset += Vector<byte>.Count)
            {
                Widen(new Vector<byte>(pixels.Slice(offset)), out var p0, out var p1, out var p2, out var p3);
                Accumulate(p0 - new Vector<float>(background, offset), included, offset, ref sum, ref count);
                Accumulate(p1 - new Vector<float>(background, offset + Vector<float>.Count), included, offset + Vector<float>.Count, ref sum, ref count);
                Accumulate(p2 - new Vector<float>(background, offset + Vector<float>.Count * 2), included, offset + Vector<float>.Count * 2, ref sum, ref count);
                Accumulate(p3 - new Vector<float>(background, offset + Vector<float>.Count * 3), included, offset + Vector<float>.Count * 3, ref sum, ref count);
            }
        }
        for (; offset < pixels.Length; offset++)
        {
            if (included[offset])
            {
                sum += pixels[offset] - background[offset];
                count++;
            }
        }
        return sum;
    }

    private static void Accumulate(Vector<float> differences, bool[] included, int offset, ref double sum, ref int count)
    {
        // Preserve the scalar sum order: regrouping floating-point additions can change threshold decisions.
        for (var lane = 0; lane < Vector<float>.Count; lane++)
        {
            if (included[offset + lane])
            {
                sum += differences[lane];
                count++;
            }
        }
    }

    private static void Widen(Vector<byte> pixels, out Vector<float> first, out Vector<float> second,
        out Vector<float> third, out Vector<float> fourth)
    {
        Vector.Widen(pixels, out var low, out var high);
        Vector.Widen(low, out var p0, out var p1);
        Vector.Widen(high, out var p2, out var p3);
        first = Vector.ConvertToSingle(p0);
        second = Vector.ConvertToSingle(p1);
        third = Vector.ConvertToSingle(p2);
        fourth = Vector.ConvertToSingle(p3);
    }
}
