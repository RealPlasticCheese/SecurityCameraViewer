namespace RtspVideoHarness.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private long ticks;
    private readonly List<Timer> timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
    public int Pending => timers.Count(timer => !timer.Disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(callback, state, ticks + dueTime.Ticks);
        timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        ticks += duration.Ticks;
        foreach (var timer in timers.Where(timer => !timer.Disposed && timer.Due <= ticks).ToArray())
        {
            timer.Dispose();
            timer.Callback(timer.State);
        }
    }

    private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long Due { get; } = due;
        public bool Disposed
        {
            get; private set;
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
