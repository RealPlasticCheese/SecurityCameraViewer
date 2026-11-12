using System.Buffers;
using System.Threading.Channels;

namespace SecurityCameraViewer.Core;

/// <summary>Bridges synchronous process pipes to async consumers without occupying thread-pool workers.
/// The process owner supplies the action that interrupts a blocked read. The source is borrowed.</summary>
public sealed class ProcessOutputStream : Stream
{
    private readonly record struct Chunk(byte[] Bytes, int Count);
    private readonly Channel<Chunk> _chunks = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenRegistration _registration;
    private readonly Task _reader;
    private Chunk _current;
    private int _offset;
    private volatile bool _finished;
    private bool _disposed;
    private Task? _disposeTask;

    public ProcessOutputStream(Stream source, Action unblock, CancellationToken token, int bufferSize = 16384)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(unblock);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _registration = _stop.Token.Register(() =>
        {
            if (!_finished)
            {
                unblock();
            }
        });
        _reader = Task.Factory.StartNew(() => Produce(source, bufferSize), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Produce(Stream source, int bufferSize)
    {
        byte[]? bytes = null;
        Exception? failure = null;
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                bytes = ArrayPool<byte>.Shared.Rent(bufferSize);
                var count = source.Read(bytes, 0, bufferSize);
                _stop.Token.ThrowIfCancellationRequested();
                if (count == 0)
                {
                    break;
                }
                // Backpressure bounds storage to the producer, queue and consumer chunks.
                _chunks.Writer.WriteAsync(new(bytes, count), _stop.Token).AsTask().GetAwaiter().GetResult();
                bytes = null;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (bytes is not null)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
            _finished = true;
            _chunks.Writer.TryComplete(failure);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _stop.Token.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }
        if (_current.Bytes is null)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }
            _current = await _chunks.Reader.ReadAsync(cancellationToken);
            _offset = 0;
        }
        var count = Math.Min(buffer.Length, _current.Count - _offset);
        _current.Bytes.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _current.Count)
        {
            ArrayPool<byte>.Shared.Return(_current.Bytes);
            _current = default;
        }
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException(); set => throw new NotSupportedException();
    }
    public override void Flush()
    {
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override ValueTask DisposeAsync() => new(_disposeTask ??= StopAsync());

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    private async Task StopAsync()
    {
        _disposed = true;
        await _stop.CancelAsync();
        await _reader;
        if (_current.Bytes is not null)
        {
            ArrayPool<byte>.Shared.Return(_current.Bytes);
            _current = default;
        }
        while (_chunks.Reader.TryRead(out var chunk))
        {
            ArrayPool<byte>.Shared.Return(chunk.Bytes);
        }
        _registration.Dispose();
        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
