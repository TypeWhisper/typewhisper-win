using System.Diagnostics;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Keeps the History search index in line with History, in the background.</summary>
/// <remarks>
/// <para>
/// The index is on while its file exists: a module turns it on with <see cref="PrepareAsync"/>, and it stays on across
/// restarts until <see cref="DisableAsync"/> or "Delete all data" removes the file. While it is off, History changes
/// cost one comparison of a timestamp.
/// </para>
/// <para>
/// Every pass compares History as it is with the content hashes the index holds, so it converges whatever changes it
/// missed. Removed records leave the index at once, clearing History deletes the index file and starts an empty one, and
/// added or changed records are written shortly after the last change, in small batches, while no dictation runs. A
/// file that is damaged or has another format is deleted by the index and built again.
/// </para>
/// </remarks>
public sealed class HistorySearchIndexer
{
    /// <summary>Records written in one step, so a removal never waits long.</summary>
    public const int BatchSize = 50;
    // How often a batch that could not be written is tried again before the indexer waits for the next change.
    private const int UpsertRetries = 3;
    // How often per launch the index is built again after it was deleted for damage, so a file that keeps breaking
    // does not keep the PC busy.
    private const int RebuildsAfterDamage = 3;

    private readonly IHistoryService _history;
    private readonly HistorySearchIndex _index;
    private readonly Action<string, Exception> _report;
    private readonly Func<bool> _isBusy;
    private readonly TimeSpan _upsertDelay;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _startupDelay;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    // Each waiter with the number of the last pass that started before it: a later pass that finds nothing to do completes it.
    private readonly List<(TaskCompletionSource Waiter, long After)> _waiters = [];
    private long _passes;
    private Task? _worker;
    private volatile bool _enabled;
    private volatile bool _disableRequested;
    private volatile bool _damaged;
    private volatile bool _stopped;
    private volatile bool _subscribed;
    private int _flushes;
    private long _lastChange;
    // Worker only: the content hashes the index holds, or null when they must be read again.
    private Dictionary<string, string>? _known;
    private bool _integrityCheckDue;
    private int _failures;
    private int _rebuilds;
    private bool _stalled;

    /// <param name="history">The History whose records the index holds.</param>
    /// <param name="index">The index.</param>
    /// <param name="report">Reports a failure with a stage name; never the record text.</param>
    /// <param name="isBusy">True while indexing would compete with a dictation or a recording.</param>
    /// <param name="upsertDelay">How long changed records wait for further changes before they are written.</param>
    /// <param name="retryDelay">How long the indexer waits while busy or after a failure.</param>
    /// <param name="startupDelay">How long <see cref="Start"/> waits before it checks an existing index, unless History changes first.</param>
    public HistorySearchIndexer(IHistoryService history, HistorySearchIndex index, Action<string, Exception> report,
        Func<bool>? isBusy = null, TimeSpan? upsertDelay = null, TimeSpan? retryDelay = null, TimeSpan? startupDelay = null)
    {
        _history = history;
        _index = index;
        _report = report;
        _isBusy = isBusy ?? (() => false);
        _upsertDelay = upsertDelay ?? TimeSpan.FromSeconds(2);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(30);
        _index.Damaged += () =>
        {
            _damaged = true;
            Wake();
        };
    }

    /// <summary>Whether the index is on: reads may return records.</summary>
    public bool IsEnabled => _enabled && !_stopped;

    /// <summary>
    /// Starts keeping an existing index in step, after the startup delay or with the first History change. Does
    /// nothing else when no index exists. Call once at startup.
    /// </summary>
    public void Start()
    {
        if (_stopped) return;
        bool exists;
        try { exists = _index.Exists; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("history-search.start-failed", ex); return; }
        if (!exists) return;
        _enabled = true;
        _integrityCheckDue = true;
        EnsureRunning(_startupDelay);
    }

    /// <summary>Turns the index on and waits until it holds History as it is now, also records changed a moment ago.</summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return;
        _enabled = true;
        _disableRequested = false;
        _stalled = false;
        EnsureRunning(TimeSpan.Zero);
        await WaitForQueuedWorkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns the index off and deletes it.</summary>
    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return;
        _enabled = false;
        _disableRequested = true;
        EnsureRunning(TimeSpan.Zero);
        await WaitForQueuedWorkAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the queued records are written, also those changed a moment ago. Waits longer while a dictation
    /// runs; returns when the indexer gives up on a failing write, stops, or the caller is canceled.
    /// </summary>
    public async Task WaitForQueuedWorkAsync(CancellationToken cancellationToken = default)
    {
        if (_worker is null || _stopped) return;
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (TaskCompletionSource, long) entry;
        lock (_gate)
        {
            entry = (waiter, _passes);
            _waiters.Add(entry);
            _flushes++;
        }
        Wake();
        try { await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (_waiters.Remove(entry)) _flushes--;
            }
        }
    }

    /// <summary>Ends all index work for good, waits for the running step and closes the index file.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        if (_subscribed) _history.RecordsChanged -= OnRecordsChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            try { await worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        CompleteWaiters();
        try { _index.Close(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("history-search.close-failed", ex); }
    }

    private void EnsureRunning(TimeSpan firstDelay)
    {
        lock (_gate)
        {
            if (!_subscribed)
            {
                _subscribed = true;
                _history.RecordsChanged += OnRecordsChanged;
            }
            if (_worker is not null)
            {
                Wake();
                return;
            }
            _worker = Task.Run(() => RunAsync(firstDelay));
        }
    }

    // Raised on the saving thread, possibly in bursts; one pass covers them all.
    private void OnRecordsChanged()
    {
        Interlocked.Exchange(ref _lastChange, Stopwatch.GetTimestamp());
        _stalled = false;
        if (_enabled) Wake();
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task<bool> SleepAsync(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return false;
        try { return await _wake.WaitAsync(delay, _stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task RunAsync(TimeSpan firstDelay)
    {
        await SleepAsync(firstDelay).ConfigureAwait(false);
        var again = true;
        while (!_stopped)
        {
            if (!again)
            {
                try { await _wake.WaitAsync(_stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            long pass;
            lock (_gate) pass = ++_passes;
            try { again = await PassAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Unexpected: wait for the next change instead of spinning.
                _report("history-search.pass-failed", ex);
                _stalled = true;
                again = false;
            }
            // Idle: nothing due, the index is off, or the indexer gave up until the next change.
            if (!again) CompleteWaiters(pass);
        }
        CompleteWaiters();
    }

    // One step towards History as it is. Returns true when more work is due at once.
    private async Task<bool> PassAsync()
    {
        if (_disableRequested)
        {
            _disableRequested = false;
            _known = null;
            if (!_index.Destroy()) _report("history-search.delete-failed", new IOException("The search index could not be deleted."));
            return false;
        }
        if (!_enabled || _stalled) return false;
        if (_damaged)
        {
            _damaged = false;
            _known = null;
            if (++_rebuilds > RebuildsAfterDamage)
            {
                // No file is left that this run would no longer keep in step; the index stays off until a module
                // turns it on again.
                _enabled = false;
                if (!_index.Destroy()) _report("history-search.delete-failed", new IOException("The search index could not be deleted."));
                return false;
            }
        }

        try { await _history.EnsureLoadedAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without History the index cannot tell removed records from unread ones, so it waits.
            _report("history-search.history-unavailable", ex);
            _stalled = true;
            return false;
        }
        var records = _history.Records;

        if (_known is null)
        {
            _index.Create();
            if (_integrityCheckDue)
            {
                _integrityCheckDue = false;
                try { _index.CheckIntegrity(); }
                catch (HistorySearchIndexException ex)
                {
                    _report("history-search.inconsistent", ex);
                    Rebuild();
                    return true;
                }
            }
            _known = new Dictionary<string, string>(_index.ContentHashes(), StringComparer.Ordinal);
            if (_damaged) return true;
        }

        // Clearing History leaves no text of it in the index's file.
        if (records.Count == 0 && _known.Count > 0)
        {
            Rebuild();
            return true;
        }

        var wanted = new Dictionary<string, TranscriptionRecord>(StringComparer.Ordinal);
        foreach (var record in records)
            if (HistorySearchRecord.Indexes(record)) wanted.TryAdd(record.Id, record);

        var removed = _known.Keys.Where(id => !wanted.ContainsKey(id)).ToArray();
        if (removed.Length > 0)
        {
            try { _index.Remove(removed); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A removed record must not stay searchable, so the index is built again without it. A damaged file
                // was already deleted by the index.
                _report("history-search.remove-failed", ex);
                if (ex is not HistorySearchIndexException { IsUnusableFile: true }) Rebuild();
                return true;
            }
            foreach (var id in removed) _known.Remove(id);
        }

        var due = wanted.Values.Where(record => !_known.TryGetValue(record.Id, out var hash) || hash != HistorySearchRecord.HashOf(record))
            .ToArray();
        if (due.Length == 0)
        {
            _failures = 0;
            return false;
        }

        // Changed records wait until History has been quiet for a moment, unless someone waits for them.
        var flushing = Volatile.Read(ref _flushes) > 0;
        var quiet = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastChange));
        if (!flushing && quiet < _upsertDelay)
        {
            await SleepAsync(_upsertDelay - quiet).ConfigureAwait(false);
            return true;
        }
        if (_isBusy())
        {
            await SleepAsync(_retryDelay).ConfigureAwait(false);
            return true;
        }

        var batch = due.Take(BatchSize).Select(HistorySearchRecord.From).OfType<HistorySearchRecord>().ToArray();
        try
        {
            _index.Upsert(batch);
            _failures = 0;
            foreach (var record in batch) _known[record.Id] = record.ContentHash;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _report("history-search.update-failed", ex);
            // The index deleted a damaged file and asks for the rebuild itself.
            if (ex is HistorySearchIndexException { IsUnusableFile: true }) return true;
            if (++_failures > UpsertRetries)
            {
                _failures = 0;
                _stalled = true;
                return false;
            }
            await SleepAsync(_retryDelay).ConfigureAwait(false);
        }
        return true;
    }

    // Deletes the index file and starts an empty one, which the next passes fill from History.
    private void Rebuild()
    {
        _known = null;
        if (!_index.Destroy()) _report("history-search.delete-failed", new IOException("The search index could not be deleted."));
    }

    // Completes the waiters that registered before `pass` started, or every waiter.
    private void CompleteWaiters(long pass = long.MaxValue)
    {
        TaskCompletionSource[] waiters;
        lock (_gate)
        {
            waiters = _waiters.Where(entry => entry.After < pass).Select(entry => entry.Waiter).ToArray();
            _waiters.RemoveAll(entry => entry.After < pass);
            _flushes = _waiters.Count;
        }
        foreach (var waiter in waiters) waiter.TrySetResult();
    }
}
