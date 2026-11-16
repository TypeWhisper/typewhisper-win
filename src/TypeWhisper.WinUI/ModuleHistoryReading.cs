using System.Collections.Frozen;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.ModuleKit;

namespace TypeWhisper.WinUI;

// The difference between History's records at two saved changes, by identifier and record instance. History
// replaces a record when it changes and keeps the instance otherwise, so a new instance means new content.
internal sealed class ModuleHistoryChanges
{
    private Dictionary<string, TranscriptionRecord> _known = new(StringComparer.Ordinal);

    // Sets the records the next Update compares with, without reporting them.
    internal void Reset(IEnumerable<TranscriptionRecord> records) => _known = Map(records);

    // The identifiers the next Update compares with.
    internal IReadOnlySet<string> KnownIds => _known.Keys.ToFrozenSet(StringComparer.Ordinal);

    internal IReadOnlyList<ModuleHistoryChange> Update(IEnumerable<TranscriptionRecord> records)
    {
        var previous = _known;
        var current = _known = Map(records);
        if (current.Count == 0) return previous.Count == 0 ? [] : [new ModuleHistoryChange.Cleared()];
        var removed = previous.Keys.Where(id => !current.ContainsKey(id)).ToFrozenSet(StringComparer.Ordinal);
        var upserted = current
            .Where(entry => !previous.TryGetValue(entry.Key, out var before) || !ReferenceEquals(before, entry.Value))
            .Select(entry => entry.Key).ToFrozenSet(StringComparer.Ordinal);
        var changes = new List<ModuleHistoryChange>(2);
        if (removed.Count > 0) changes.Add(new ModuleHistoryChange.Removed(removed));
        if (upserted.Count > 0) changes.Add(new ModuleHistoryChange.Upserted(upserted));
        return changes;
    }

    private static Dictionary<string, TranscriptionRecord> Map(IEnumerable<TranscriptionRecord> records)
    {
        var map = new Dictionary<string, TranscriptionRecord>(StringComparer.Ordinal);
        foreach (var record in records) map.TryAdd(record.Id, record);
        return map;
    }
}

// IHistoryReading over the History service. Changes are compared on the UI thread, where modules receive them.
internal sealed class ModuleHistoryReading : IHistoryReading
{
    private readonly IHistoryService _history;
    private readonly Func<string?, string?> _resolveAudioPath;
    private readonly Action<string> _showRecord;
    private readonly Action<Action> _post;
    private readonly Action<string, Exception> _report;
    private readonly ModuleHistoryChanges _changes = new();
    private readonly List<(ModuleObservation Observation, Action<ModuleHistoryChange> Handler)> _observers = [];
    private int _subscribed;
    private int _comparePending;
    // UI thread only: whether _changes holds the records of an earlier read.
    private bool _tracking;

    internal ModuleHistoryReading(IHistoryService history, Func<string?, string?> resolveAudioPath, Action<string> showRecord,
        Action<Action> post, Action<string, Exception> report)
    {
        _history = history;
        _resolveAudioPath = resolveAudioPath;
        _showRecord = showRecord;
        _post = post;
        _report = report;
    }

    public async Task<IReadOnlyList<ModuleHistoryRecord>> RecordsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        await _history.EnsureLoadedAsync().WaitAsync(cancellationToken);
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        // Resolving kept audio verifies the file's content, so this stays off the UI thread.
        return await Task.Run(() =>
        {
            var records = new List<ModuleHistoryRecord>();
            foreach (var record in _history.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (wanted.Contains(record.Id) && IsComplete(record)) records.Add(ToModuleRecord(record, _resolveAudioPath));
            }
            return (IReadOnlyList<ModuleHistoryRecord>)records.AsReadOnly();
        }, cancellationToken);
    }

    // Every record History still has, whatever its state, so a module does not drop data for a record that is only
    // being processed again.
    public async Task<IReadOnlySet<string>> ExistingRecordIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return FrozenSet<string>.Empty;
        await _history.EnsureLoadedAsync().WaitAsync(cancellationToken);
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return await Task.Run(() => (IReadOnlySet<string>)_history.Records.Select(record => record.Id).Where(wanted.Contains)
            .ToFrozenSet(StringComparer.Ordinal), cancellationToken);
    }

    public IDisposable ObserveChanges(Action<ModuleHistoryChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var observation = new ModuleObservation(_post, _report, "modules.history.observer-failed");
        var entry = (observation, handler);
        observation.Unsubscribe = () => { lock (_observers) _observers.Remove(entry); };
        // Following History starts with the first observer and lasts as long as the app. Every observer first gets
        // Current: the first ones from the baseline, later ones when they join on the UI thread. A later observer joins
        // the list only then, so no comparison queued before it can reach it ahead of its Current.
        if (Interlocked.Exchange(ref _subscribed, 1) == 0)
        {
            lock (_observers) _observers.Add(entry);
            _ = StartTrackingAsync();
        }
        else _post(() =>
        {
            if (observation.IsDisposed) return;
            if (_tracking) observation.Deliver(() => handler(new ModuleHistoryChange.Current(_changes.KnownIds)));
            lock (_observers) _observers.Add(entry);
        });
        return observation;
    }

    public void ShowRecord(string id)
    {
        if (!string.IsNullOrEmpty(id)) _showRecord(id);
    }

    // A record a module may read: complete, not failed and not still arriving from another device.
    internal static bool IsComplete(TranscriptionRecord record) => TypeWhisper.Presentation.HistorySearchRecord.Indexes(record);

    internal static ModuleHistoryRecord ToModuleRecord(TranscriptionRecord record, Func<string?, string?> resolveAudioPath) => new(
        record.Id,
        RecordedAt(record.Timestamp),
        TypeWhisper.Presentation.HistorySearchRecord.SourceOf(record),
        record.DisplayText,
        string.IsNullOrWhiteSpace(record.Language) ? null : record.Language,
        record.DurationSeconds,
        string.IsNullOrWhiteSpace(record.AppName) ? null : record.AppName,
        string.IsNullOrEmpty(record.AudioFileName) ? null : resolveAudioPath(record.AudioFileName));

    // History stores UTC; a timestamp read back without a kind is UTC too.
    internal static DateTimeOffset RecordedAt(DateTime timestamp) => TypeWhisper.Presentation.HistorySearchRecord.RecordedAtOf(timestamp);

    private async Task StartTrackingAsync()
    {
        _history.RecordsChanged += OnRecordsChanged;
        try { await _history.EnsureLoadedAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("modules.history.load-failed", ex); }
        // The baseline goes out as Current, so whatever History did before it, saves and removals included, reaches
        // observers through it; every later save is compared with it. No save can fall between the two.
        _post(() =>
        {
            IReadOnlyList<TranscriptionRecord> baseline;
            try { baseline = _history.Records; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _report("modules.history.read-failed", ex); baseline = []; }
            _changes.Reset(baseline);
            _tracking = true;
            Deliver([new ModuleHistoryChange.Current(_changes.KnownIds)]);
        });
    }

    // Raised on the saving thread, possibly in bursts: one comparison on the UI thread covers them all.
    private void OnRecordsChanged()
    {
        if (Interlocked.Exchange(ref _comparePending, 1) != 0) return;
        _post(() =>
        {
            Volatile.Write(ref _comparePending, 0);
            // Before the baseline exists, the queued baseline callback compares and delivers instead.
            if (_tracking) Compare();
        });
    }

    private void Compare()
    {
        IReadOnlyList<TranscriptionRecord> records;
        try { records = _history.Records; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report("modules.history.read-failed", ex); return; }
        Deliver(_changes.Update(records));
    }

    private void Deliver(IReadOnlyList<ModuleHistoryChange> changes)
    {
        if (changes.Count == 0) return;
        (ModuleObservation Observation, Action<ModuleHistoryChange> Handler)[] observers;
        lock (_observers) observers = [.. _observers];
        foreach (var change in changes)
            foreach (var (observation, handler) in observers)
                observation.Deliver(() => handler(change));
    }
}
