namespace SecurityCameraViewer.Web;

/// <summary>Reference-counted preview consumers. Only the last release cancels capture.</summary>
public sealed class PreviewDemand(bool enabled = true) : IDisposable
{
    private readonly object _sync = new();
    private TaskCompletionSource<CancellationToken> _available = NewSignal();
    private CancellationTokenSource? _active;
    private int _consumers;
    private bool _disposed;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return !_disposed && (!enabled || _consumers > 0);
            }
        }
    }

    public Task<CancellationToken> WaitAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return !enabled ? Task.FromResult(CancellationToken.None) :
                _consumers > 0 ? Task.FromResult(_active!.Token) : _available.Task.WaitAsync(token);
        }
    }

    public IDisposable Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled)
            {
                return EmptyLease.Instance;
            }
            if (_consumers++ == 0)
            {
                _active = new();
                _available.TrySetResult(_active.Token);
            }
            return new Lease(this);
        }
    }

    private void Release()
    {
        CancellationTokenSource? ending = null;
        lock (_sync)
        {
            if (!_disposed && --_consumers == 0)
            {
                ending = _active;
                _active = null;
                _available = NewSignal();
            }
        }
        ending?.Cancel();
        ending?.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? ending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ending = _active;
            _active = null;
            _available.TrySetCanceled();
        }
        ending?.Cancel();
        ending?.Dispose();
    }

    private static TaskCompletionSource<CancellationToken> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Lease(PreviewDemand owner) : IDisposable
    {
        private PreviewDemand? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }

    internal sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
