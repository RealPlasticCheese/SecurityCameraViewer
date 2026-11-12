using System.Diagnostics;

namespace SecurityCameraViewer.Core;

/// <summary>Shares bounded RTSP checks between web viewing and recording. Never starts a decoder while idle.</summary>
public sealed class BatteryCameraAvailability(Func<CancellationToken, Task<ProbeResult>> probe, TimeSpan interval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private TaskCompletionSource _checkNow = NewSignal();
    private ProbeResult? _result;
    private long _checkedAt;
    private bool _verified;
    public bool Verified => Volatile.Read(ref _verified);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void VideoArrived() => Volatile.Write(ref _verified, true);

    public string WaitingMessage => Verified ? "Waiting for activity" : "Waiting for activity · stream not yet verified; trigger motion to test the URL";

    public void CheckNow()
    {
        lock (_sync)
        {
            _checkedAt = 0;
            var previous = _checkNow;
            _checkNow = NewSignal();
            previous.TrySetResult();
        }
    }

    public void StreamEnded()
    {
        lock (_sync)
        {
            // Keep the last check's deadline: repeated short streams must not create a busy loop.
            if (_result?.Success == true)
            {
                _result = null;
            }
        }
    }

    public async Task RetryDelayAsync(CancellationToken token)
    {
        Task signal;
        lock (_sync)
        {
            if (_checkedAt == 0)
            {
                return;
            }

            signal = _checkNow.Task;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        await Task.WhenAny(Task.Delay(interval, wait.Token), signal).WaitAsync(token);
        await wait.CancelAsync();
    }

    public async Task WaitAsync(Action<ProbeResult> report, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ProbeResult? result;
            TimeSpan delay;
            Task signal;
            await _gate.WaitAsync(token);
            try
            {
                lock (_sync)
                {
                    delay = _checkedAt == 0 ? TimeSpan.Zero : interval - Stopwatch.GetElapsedTime(_checkedAt);
                    signal = _checkNow.Task;
                    result = _result;
                }
                if (delay <= TimeSpan.Zero)
                {
                    try
                    {
                        result = await probe(token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // A failed device probe must not terminate its playback/recording worker.
                        result = new(false, 0, "Camera availability check failed. Retrying…");
                    }
                    lock (_sync)
                    {
                        _result = result;
                        _checkedAt = Stopwatch.GetTimestamp();
                        delay = interval;
                    }
                }
            }
            finally { _gate.Release(); }
            if (result is not null)
            {
                report(result);
                if (result.Success)
                {
                    return;
                }
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            await Task.WhenAny(Task.Delay(delay, wait.Token), signal).WaitAsync(token);
            await wait.CancelAsync();
        }
    }
}
