namespace SecurityCameraViewer.Core;

/// <summary>Monotonic, independently testable stream recovery decisions.</summary>
public sealed class CameraRecovery
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(4);
    private readonly CameraRetrySchedule _schedule;
    private TimeSpan _attemptStarted;
    private TimeSpan? _healthySince;
    private int _failures;
    public TimeSpan? LastFrame
    {
        get; private set;
    }
    public TimeSpan? RetryAt
    {
        get; private set;
    }
    public bool HasFrameThisAttempt
    {
        get; private set;
    }

    public CameraRecovery(string retryDelaysSeconds = CameraRetrySchedule.DefaultDelaysSeconds)
    {
        _schedule = CameraRetrySchedule.Parse(retryDelaysSeconds);
    }

    public void StartAttempt(TimeSpan now)
    {
        _attemptStarted = now;
        _healthySince = null;
        RetryAt = null;
        HasFrameThisAttempt = false;
    }

    public void FrameArrived(TimeSpan now)
    {
        LastFrame = now;
        HasFrameThisAttempt = true;
        // A transient failure may recover without a new connection. Cancel that pending retry.
        RetryAt = null;
        _healthySince ??= now;
        if (now - _healthySince >= TimeSpan.FromSeconds(30))
        {
            _failures = 0;
        }
    }

    public bool IsStale(TimeSpan now) => !HasFrameThisAttempt || now - LastFrame >= StaleAfter;
    public bool IsStalled(TimeSpan now) => now - (HasFrameThisAttempt ? LastFrame!.Value : _attemptStarted) >= StallTimeout;
    public bool ShouldRetry(TimeSpan now) => RetryAt is not null && now >= RetryAt;
    public TimeSpan FrameAge(TimeSpan now) => LastFrame is null ? TimeSpan.Zero : now - LastFrame.Value;

    public void Failed(TimeSpan now)
    {
        if (RetryAt is not null)
        {
            return;
        }

        _healthySince = null;
        RetryAt = now + _schedule.GetDelay(_failures);
        if (_failures < int.MaxValue)
        {
            _failures++;
        }
    }

    public async Task WaitForRetryAsync(Func<TimeSpan> elapsed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (RetryAt is { } retryAt && retryAt > elapsed())
        {
            // Chunk long configured delays to stay within the platform timer's range.
            var remaining = retryAt - elapsed();
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token);
            }
        }
    }
}
