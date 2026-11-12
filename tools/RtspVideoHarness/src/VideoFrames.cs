namespace RtspVideoHarness;

public static class BlackFrames
{
    public static byte[] Create(VideoFormat format)
    {
        var frame = new byte[format.FrameBytes];
        var lumaBytes = checked(format.Width * format.Height);
        // Studio-range YUV420: black luma, neutral chroma.
        frame.AsSpan(0, lumaBytes).Fill(16);
        frame.AsSpan(lumaBytes).Fill(128);
        return frame;
    }

    public static long Count(TimeSpan duration, int framesPerSecond)
    {
        if (duration < TimeSpan.Zero || framesPerSecond < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return checked((long)decimal.Ceiling((decimal)duration.Ticks * framesPerSecond / TimeSpan.TicksPerSecond));
    }
}

public static class VideoFrames
{
    public static async Task<long> CopyAsync(Stream input, int frameBytes,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeFrame, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        var frame = new byte[frameBytes];
        long count = 0;
        while (true)
        {
            var bytes = 0;
            while (bytes < frame.Length)
            {
                var read = await input.ReadAsync(frame.AsMemory(bytes), cancellationToken);
                if (read == 0)
                {
                    if (bytes != 0)
                    {
                        throw new InvalidDataException("FFmpeg returned an incomplete video frame.");
                    }

                    return count;
                }

                bytes += read;
            }

            await writeFrame(frame, cancellationToken);
            count++;
        }
    }
}

public sealed class FramePacer(int framesPerSecond, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private long? lastFrame;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lastFrame is { } previous)
        {
            var remaining = TimeSpan.FromSeconds(1d / framesPerSecond) - time.GetElapsedTime(previous);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, time, cancellationToken);
            }
        }

        lastFrame = time.GetTimestamp();
    }
}
