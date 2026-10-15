using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>Publishes validated policy snapshots from one source and keeps them fresh.</summary>
/// <remarks>
/// Reads happen on <see cref="Start"/>, on explicit <see cref="Refresh"/>, after a debounced source change
/// and on a fallback interval. A new snapshot is published only when its content differs; an unreadable
/// source keeps the last readable values but marks them <see cref="ManagedPolicyReadStatus.Unreadable"/>.
/// <see cref="Changed"/> is raised on the refreshing thread while the service lock is held, so handlers
/// stay short and marshal to their own threads.
/// </remarks>
public sealed class ManagedPolicyService : IDisposable
{
    /// <summary>How often the source is re-read without a change notification.</summary>
    public static readonly TimeSpan DefaultFallbackInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long bursts of change notifications are collected before one read.</summary>
    public static readonly TimeSpan DefaultChangeDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IManagedPolicySource _source;
    private readonly TimeSpan _fallbackInterval;
    private readonly TimeSpan _changeDebounce;
    private readonly Action<string, Exception?>? _reportFailure;
    private readonly object _lock = new();
    private ManagedPolicySnapshot _current = ManagedPolicySnapshot.Empty;
    private Timer? _fallbackTimer;
    private Timer? _debounceTimer;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a service over a source. Nothing is read until <see cref="Start"/> or <see cref="Refresh"/>.</summary>
    /// <param name="source">The raw policy source.</param>
    /// <param name="fallbackInterval">The periodic re-read interval; zero disables it.</param>
    /// <param name="changeDebounce">The delay after a change notification before reading.</param>
    /// <param name="reportFailure">Receives a stage name and the exception of a failed read or handler.</param>
    public ManagedPolicyService(
        IManagedPolicySource source,
        TimeSpan? fallbackInterval = null,
        TimeSpan? changeDebounce = null,
        Action<string, Exception?>? reportFailure = null)
    {
        _source = source;
        _fallbackInterval = fallbackInterval ?? DefaultFallbackInterval;
        _changeDebounce = changeDebounce ?? DefaultChangeDebounce;
        _reportFailure = reportFailure;
    }

    /// <summary>The latest published snapshot; <see cref="ManagedPolicySnapshot.Empty"/> before the first read.</summary>
    public ManagedPolicySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Raised with each newly published snapshot.</summary>
    public event Action<ManagedPolicySnapshot>? Changed;

    /// <summary>Reads once, then follows source changes and the fallback interval. Idempotent.</summary>
    public ManagedPolicySnapshot Start()
    {
        lock (_lock)
        {
            if (_disposed) return _current;
            if (_started) return _current;
            _started = true;
            _source.Changed += OnSourceChanged;
        }
        var snapshot = Refresh();
        lock (_lock)
        {
            if (!_disposed && _fallbackInterval > TimeSpan.Zero)
                _fallbackTimer = new Timer(_ => RefreshSafely("policy.refresh-failed"), null, _fallbackInterval, _fallbackInterval);
        }
        return snapshot;
    }

    /// <summary>Reads the source now and publishes the result when it differs from the current snapshot.</summary>
    public ManagedPolicySnapshot Refresh()
    {
        lock (_lock)
        {
            if (_disposed) return _current;
            var previous = _current;
            var reading = ReadSource();
            var candidate = ManagedPolicyValidator.Validate(reading, previous.Revision + 1, DateTimeOffset.UtcNow);
            if (candidate.Status == ManagedPolicyReadStatus.Unreadable)
                candidate = candidate.RetainingValuesFrom(previous);
            // The first read is always published so ReadAt and Revision show that policy was consulted.
            if (previous.Revision > 0 && candidate.ContentEquals(previous)) return previous;
            Volatile.Write(ref _current, candidate);
            try { Changed?.Invoke(candidate); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _reportFailure?.Invoke("policy.changed-handler-failed", ex); }
            return candidate;
        }
    }

    /// <summary>Stops following the source. The last snapshot stays readable.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_started) _source.Changed -= OnSourceChanged;
            _fallbackTimer?.Dispose();
            _debounceTimer?.Dispose();
        }
    }

    private ManagedPolicyReading ReadSource()
    {
        try { return _source.Read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _reportFailure?.Invoke("policy.read-failed", ex);
            return ManagedPolicyReading.Unreadable(ex.GetType().Name);
        }
    }

    private void OnSourceChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _debounceTimer ??= new Timer(_ => RefreshSafely("policy.refresh-failed"), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(_changeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RefreshSafely(string stage)
    {
        try { Refresh(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _reportFailure?.Invoke(stage, ex); }
    }
}
