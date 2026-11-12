using System.Reflection;
using SecurityCameraViewer.Core;

internal static class StreamProcessingTests
{
    public static async Task JpegAsync()
    {
        byte[] bytes = [8, 0xff, 9, 0xff, 0xd8, 1, 0xff, 0, 0xff, 0xff, 0xd9, 7, 0xff, 0xd8, 0xff, 0xd9, 0xff, 0xd8, 1];
        byte[][] expected = [[0xff, 0xd8, 1, 0xff, 0, 0xff, 0xff, 0xd9], [0xff, 0xd8, 0xff, 0xd9]];
        for (var chunk = 1; chunk <= bytes.Length; chunk++)
        {
            var frames = new List<byte[]>();
            await JpegFrames.ReadAsync(new ChunkStream(bytes, chunk), frames.Add, CancellationToken.None);
            Check(frames.Count == 2 && frames.Zip(expected).All(pair => pair.First.SequenceEqual(pair.Second)), "JPEG markers, noise, stuffing, and incomplete tails survive every split", false);
        }
        foreach (var input in new byte[][] { [], [0xff], [0xff, 0xd8], [0xff, 0xd9], [0xff, 0xd8, 0xff] })
        {
            await JpegFrames.ReadAsync(new MemoryStream(input), _ => throw new Exception("Partial JPEG published"), CancellationToken.None);
        }
        var exact = new byte[JpegFrames.MaximumFrameBytes];
        exact[0] = 0xff;
        exact[1] = 0xd8;
        exact[^2] = 0xff;
        exact[^1] = 0xd9;
        byte[]? complete = null;
        await JpegFrames.ReadAsync(new ChunkStream(exact, 16381), frame => complete = frame, CancellationToken.None);
        Check(complete is not null && complete.SequenceEqual(exact), "JPEG accepts exactly the technical frame-size boundary");
        var oversized = new byte[exact.Length + 1];
        oversized[0] = 0xff;
        oversized[1] = 0xd8;
        await ThrowsAsync<InvalidDataException>(() => JpegFrames.ReadAsync(new MemoryStream(oversized), _ => throw new Exception("Oversized image published"), CancellationToken.None));
        await ThrowsAsync<OperationCanceledException>(() => JpegFrames.ReadAsync(new MemoryStream(exact), _ => { }, new CancellationToken(true)));
        await ThrowsAsync<IOException>(() => JpegFrames.ReadAsync(new ThrowingStream(), _ => { }, CancellationToken.None));
        await ThrowsAsync<InvalidOperationException>(() => JpegFrames.ReadAsync(new MemoryStream([0xff, 0xd8, 0xff, 0xd9]), _ => throw new InvalidOperationException(), CancellationToken.None));
        Check(true, "JPEG rejection, input failure, cancellation, and receiver failure propagate");
    }

    public static async Task PumpAsync()
    {
        var bytes = Enumerable.Range(0, 100003).Select(index => (byte)index).ToArray();
        foreach (var size in new[] { 1, 7, 16384 })
        {
            using var source = new ChunkStream(bytes, 13);
            await using var pump = CreatePump(source, () => { }, CancellationToken.None, size);
            var output = new MemoryStream();
            await pump.CopyToAsync(output);
            Check(output.ToArray().SequenceEqual(bytes), "Dedicated pipe reader preserves partial reads and EOF", false);
            Check(pump.CanRead && !pump.CanSeek && !pump.CanWrite, "Pipe reader advertises read-only stream capabilities", false);
            Throws<NotSupportedException>(() => _ = pump.Length);
            Throws<NotSupportedException>(() => _ = pump.Position);
            Throws<NotSupportedException>(() => pump.Position = 1);
            Throws<NotSupportedException>(() => pump.Seek(0, SeekOrigin.Begin));
            Throws<NotSupportedException>(() => pump.SetLength(1));
            Throws<NotSupportedException>(() => pump.Write([], 0, 0));
            pump.Flush();
            Check(await pump.ReadAsync(Memory<byte>.Empty) == 0 && pump.Read(new byte[1], 0, 1) == 0, "Pipe reader handles empty and synchronous reads at EOF", false);
        }
        await using (var pump = CreatePump(new ThrowingStream(), () => { }, CancellationToken.None, 16))
        {
            await ThrowsAsync<IOException>(async () => Check(await pump.ReadAsync(new byte[1]) == 0, "Expected failing read", false));
        }
        using var blocked = new BlockingStream();
        using var cancellation = new CancellationTokenSource();
        await using (var pump = CreatePump(blocked, blocked.Release.Set, cancellation.Token, 16))
        {
            var read = pump.ReadAsync(new byte[1]).AsTask();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => await read);
        }
        using var disposedSource = new BlockingStream();
        var disposed = CreatePump(disposedSource, disposedSource.Release.Set, CancellationToken.None, 16);
        await disposedSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await disposed.DisposeAsync();
        await disposed.DisposeAsync();
        await ThrowsAsync<ObjectDisposedException>(async () => Check(await disposed.ReadAsync(new byte[1]) == 0, "Expected disposed read", false));
        Throws<ArgumentOutOfRangeException>(() => CreatePump(new MemoryStream(), () => { }, CancellationToken.None, 0));
        Throws<ArgumentNullException>(() => CreatePump(null!, () => { }, CancellationToken.None, 16));
        Throws<ArgumentNullException>(() => CreatePump(new MemoryStream(), null!, CancellationToken.None, 16));
        using (var queued = CreatePump(new MemoryStream(bytes), () => { }, CancellationToken.None, 16))
        {
            var target = new byte[1];
            Check(await queued.ReadAsync(target, 0, 1, CancellationToken.None) == 1 && target[0] == bytes[0], "Array-based reads preserve partially consumed chunks", false);
            await ThrowsAsync<OperationCanceledException>(async () => _ = await queued.ReadAsync(target, new CancellationToken(true)));
            await Task.Delay(20); // Producer is backpressured behind current and queued chunks.
        }
        using var perReadSource = new BlockingStream();
        await using (var perRead = CreatePump(perReadSource, perReadSource.Release.Set, CancellationToken.None, 16))
        {
            using var readCancellation = new CancellationTokenSource();
            var read = perRead.ReadAsync(new byte[1], readCancellation.Token).AsTask();
            await perReadSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            readCancellation.Cancel();
            await ThrowsAsync<OperationCanceledException>(async () => await read);
            perReadSource.Release.Set();
            Check(await perRead.ReadAsync(new byte[1]) == 0, "Cancelling one read leaves the stream usable", false);
        }
        Check(true, "Dedicated reader cancellation/disposal unblock I/O and propagate errors without leaking workers");
    }

    private static Stream CreatePump(Stream source, Action unblock, CancellationToken token, int size)
    {
        var type = typeof(JpegFrames).Assembly.GetType("SecurityCameraViewer.Core.ProcessOutputStream", throwOnError: true)!;
        try
        {
            return (Stream)Activator.CreateInstance(type, source, unblock, token, size)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    public static void Check(bool condition, string message, bool print = true)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + message);
        }
        if (print)
        {
            Console.WriteLine("PASS: " + message);
        }
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class ChunkStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(chunk, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Expected source failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("Expected source failure");
    }

    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public override int Read(byte[] buffer, int offset, int count)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Reader was not unblocked");
            }
            return 0;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Release.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
