namespace SecurityCameraViewer.Core;

/// <summary>Reads FFmpeg's JPEG stream, retaining only complete, bounded frames.</summary>
internal static class JpegFrames
{
    internal const int MaximumFrameBytes = 4 * 1024 * 1024;

    public static async Task ReadAsync(Stream stream, Action<byte[]> receive, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        using var frame = new MemoryStream();
        var previous = -1;
        var inFrame = false;
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            using var measurement = ProcessingMetrics.Measure("jpeg.parse_chunk");
            var start = inFrame ? 0 : -1;
            for (var i = 0; i < count; i++)
            {
                var value = buffer[i];
                if (!inFrame && previous == 0xff && value == 0xd8)
                {
                    frame.SetLength(0);
                    frame.WriteByte(0xff);
                    inFrame = true;
                    start = i;
                }
                if (inFrame)
                {
                    if (frame.Length + i - start + 1 > MaximumFrameBytes)
                    {
                        throw new InvalidDataException("Video frame exceeded the size limit.");
                    }

                    if (previous == 0xff && value == 0xd9)
                    {
                        frame.Write(buffer.AsSpan(start, i - start + 1));
                        receive(frame.ToArray());
                        ProcessingMetrics.RecordFrame("jpeg.completed");
                        inFrame = false;
                        start = -1;
                    }
                }
                previous = value;
            }
            if (inFrame)
            {
                frame.Write(buffer.AsSpan(start, count - start));
            }
        }
    }
}
