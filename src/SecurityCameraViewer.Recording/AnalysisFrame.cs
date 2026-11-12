using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Borrowed analysis pixels. RGB frames are required for pretrained object detectors.</summary>
public sealed record AnalysisFrame(byte[] Pixels, int Width, int Height, bool IsRgb, DateTimeOffset CapturedAt)
{
    public byte[] Grayscale()
    {
        if (!IsRgb)
        {
            return Pixels;
        }

        var gray = new byte[checked(Width * Height)];
        CopyGrayscale(gray);
        return gray;
    }

    internal void CopyGrayscale(Span<byte> gray)
    {
        using var measurement = ProcessingMetrics.Measure("image.grayscale");
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)((77 * Pixels[i * 3] + 150 * Pixels[i * 3 + 1] + 29 * Pixels[i * 3 + 2]) >> 8);
        }

    }
}

public interface IFrameMotionDetector
{
    Task<MotionSample> AnalyseAsync(AnalysisFrame frame, double seconds, CancellationToken token);
}

public interface IAsyncMotionDetector : IFrameMotionDetector, IDisposable
{
}
