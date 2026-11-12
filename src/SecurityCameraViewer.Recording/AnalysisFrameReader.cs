using System.Collections.Concurrent;
using System.Threading.Channels;
using SecurityCameraViewer.Core;

namespace SecurityCameraViewer.Recording;

/// <summary>Delivers the newest complete frame. Pixels are borrowed until the callback completes.</summary>
internal static class AnalysisFrameReader
{
    public static async Task ReadAsync(Stream stream, int width, int height, bool rgb,
        Func<AnalysisFrame, Task> frame, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var length = checked(width * height * (rgb ? 3 : 1));
        // Exact-length arrays preserve AnalysisFrame's pixel-format contract. Only three owners can
        // exist at once: reader, waiting frame and callback. Dropped/finished frames return storage.
        var buffers = new ConcurrentQueue<byte[]>();
        var queue = Channel.CreateBounded<AnalysisFrame>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        }, dropped =>
        {
            ProcessingMetrics.RecordFrame("analysis.dropped");
            buffers.Enqueue(dropped.Pixels);
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var consume = ConsumeAsync();
        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var item in queue.Reader.ReadAllAsync(lifetime.Token))
                {
                    try
                    {
                        ProcessingMetrics.RecordFrame("analysis.dequeued");
                        await frame(item);
                    }
                    finally
                    {
                        buffers.Enqueue(item.Pixels);
                    }
                }
            }
            finally
            {
                await lifetime.CancelAsync();
            }
        }
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!buffers.TryDequeue(out var pixels))
                {
                    pixels = GC.AllocateUninitializedArray<byte>(length);
                }
                try
                {
                    await stream.ReadExactlyAsync(pixels, lifetime.Token);
                }
                catch (EndOfStreamException)
                {
                    buffers.Enqueue(pixels);
                    break;
                }
                catch
                {
                    buffers.Enqueue(pixels);
                    throw;
                }
                ProcessingMetrics.RecordFrame("analysis.captured");
                // This producer alone completes the writer, after leaving this loop.
                queue.Writer.TryWrite(new(pixels, width, height, rgb, DateTimeOffset.UtcNow));
            }
            queue.Writer.TryComplete();
            // EOF drains the last complete frame rather than cancelling its delivery.
            await consume;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && consume.IsCompleted)
        {
            await consume;
        }
        finally
        {
            queue.Writer.TryComplete();
            await lifetime.CancelAsync();
            try
            {
                await consume;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }
}
