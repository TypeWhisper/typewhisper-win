namespace TypeWhisper.PluginHost;

/// <summary>
/// When local models are released after inactivity. Released models load again on their next use.
/// The policy is shared by every model owner of one host; changes reschedule idle models.
/// </summary>
public sealed class ModelIdleUnloadPolicy(int seconds = ModelIdleUnloadPolicy.DefaultSeconds, TimeProvider? time = null)
{
    /// <summary>Matches the macOS default of ten minutes.</summary>
    public const int DefaultSeconds = 600;
    /// <summary>Keeps models loaded until they are unloaded explicitly.</summary>
    public const int Never = 0;
    /// <summary>Releases models right after each use.</summary>
    public const int Immediately = -1;
    private static readonly TimeSpan ImmediateDelay = TimeSpan.FromMilliseconds(100);
    private int _seconds = seconds;

    /// <summary>Idle seconds before release; <see cref="Never"/> or <see cref="Immediately"/> are special values.</summary>
    public int Seconds => Volatile.Read(ref _seconds);
    /// <summary>The idle delay, or null when models stay loaded.</summary>
    public TimeSpan? Delay => Seconds switch
    {
        Never => null,
        < 0 => ImmediateDelay,
        var value => TimeSpan.FromSeconds(value)
    };
    /// <summary>Vetoes a release while the host is using models, for example during a recording. Must be thread-safe.</summary>
    public Func<bool> CanUnload { get; init; } = static () => true;
    /// <summary>The clock for idle timers.</summary>
    public TimeProvider Time { get; } = time ?? TimeProvider.System;
    /// <summary>Raised off the UI thread after <see cref="Seconds"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Applies a new idle duration to loaded and future models.</summary>
    public void SetSeconds(int value)
    {
        if (Interlocked.Exchange(ref _seconds, value) == value) return;
        foreach (var subscriber in Changed?.GetInvocationList() ?? [])
            try { ((Action)subscriber)(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}

/// <summary>
/// Releases one owner's model after it stayed idle for the policy's delay. <see cref="Touch"/> after each use
/// restarts the wait. The release callback returns false when the owner is busy; it is then retried later.
/// </summary>
public sealed class ModelIdleTimer : IDisposable
{
    /// <summary>How long a vetoed or busy release waits before it is tried again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly ModelIdleUnloadPolicy _policy;
    private readonly Func<Task<bool>> _unload;
    private readonly object _sync = new();
    private ITimer? _timer;
    private long _version;
    private bool _pending;
    private bool _disposed;

    /// <summary>Creates a disarmed timer; the owner arms it with <see cref="Touch"/> once a model is loaded.</summary>
    public ModelIdleTimer(ModelIdleUnloadPolicy policy, Func<Task<bool>> unload)
    {
        _policy = policy; _unload = unload;
        _policy.Changed += Reschedule;
    }

    /// <summary>Whether a loaded model waits for its idle release.</summary>
    public bool IsPending { get { lock (_sync) return _pending; } }

    /// <summary>Records a use of a loaded model and restarts its idle wait.</summary>
    public void Touch()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _pending = true;
            ArmUnsafe(_policy.Delay);
        }
    }

    /// <summary>Forgets the loaded model, for example after an explicit unload.</summary>
    public void Cancel()
    {
        lock (_sync) { _pending = false; DisarmUnsafe(); }
    }

    private void Reschedule()
    {
        lock (_sync) if (_pending && !_disposed) ArmUnsafe(_policy.Delay);
    }

    private void ArmUnsafe(TimeSpan? delay)
    {
        DisarmUnsafe();
        if (delay is not { } due) return;
        var version = _version;
        _timer = _policy.Time.CreateTimer(_ => _ = FireAsync(version), null, due, Timeout.InfiniteTimeSpan);
    }

    // A new version invalidates callbacks of earlier timers that were already running.
    private void DisarmUnsafe()
    {
        _version++;
        _timer?.Dispose();
        _timer = null;
    }

    private async Task FireAsync(long version)
    {
        lock (_sync) if (_disposed || !_pending || version != _version) return;
        bool released;
        try { released = _policy.CanUnload() && await _unload().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { released = true; }
        lock (_sync)
        {
            // A use during the release restarted the wait for the reloaded model.
            if (_disposed || version != _version) return;
            if (released) { _pending = false; DisarmUnsafe(); }
            else ArmUnsafe(_policy.Delay is null ? null : RetryDelay);
        }
    }

    /// <summary>Stops the timer; a running release is not awaited.</summary>
    public void Dispose()
    {
        _policy.Changed -= Reschedule;
        lock (_sync) { _disposed = true; _pending = false; DisarmUnsafe(); }
    }
}
