// A clock that only moves when a test advances it; due one-shot timers fire on the advancing thread.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        Timer[] due;
        lock (_sync)
        {
            _now += by;
            due = _timers.Where(timer => timer.Due <= _now).ToArray();
            foreach (var timer in due) _timers.Remove(timer);
        }
        foreach (var timer in due) timer.Fire();
    }

    private sealed class Timer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset Due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = owner._now + dueTime;
                owner._timers.Add(this);
            }
            return true;
        }

        internal void Fire() => callback(state);
        public void Dispose() { lock (owner._sync) owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition was not reached.");
            await Task.Delay(10);
        }
    }
}
