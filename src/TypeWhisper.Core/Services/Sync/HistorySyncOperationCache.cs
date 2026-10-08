namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Remembers parsed History operation files between sync passes so unchanged files are not read again.
/// </summary>
/// <remarks>
/// One instance belongs to whoever runs History sync, so profiles and tests never share entries or keep
/// transcripts alive for each other. The cache holds at most <see cref="Capacity"/> files. When it is full,
/// entries whose file has disappeared are evicted first; if the folder still holds more files than fit, the
/// surplus is read again on every pass. That costs I/O, never correctness, and avoids the churn of a strict
/// least-recently-used order, which would evict every entry during a scan of a folder larger than the cache.
/// A different folder clears the cache, since its paths could only hold memory.
/// </remarks>
public sealed class HistorySyncOperationCache
{
    /// <summary>The default bound: the operation files of a few thousand History entries.</summary>
    public const int DefaultCapacity = 16_384;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string? _folder;
    private long _pass;
    private long _sweptPass;

    /// <summary>Creates a cache that remembers at most <paramref name="capacity"/> files.</summary>
    /// <param name="capacity">The most files to remember; at least one.</param>
    public HistorySyncOperationCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>The most files the cache remembers.</summary>
    public int Capacity { get; }

    /// <summary>The number of files currently remembered.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Forgets every file.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    // Starts a pass over the operation files below one folder. Entries of another folder are dropped.
    internal void BeginPass(string operationsPath)
    {
        lock (_gate)
        {
            if (!string.Equals(_folder, operationsPath, StringComparison.OrdinalIgnoreCase))
            {
                _entries.Clear();
                _folder = operationsPath;
            }
            _pass++;
        }
    }

    internal bool TryGet(string path, (long Length, DateTime LastWriteUtc) stamp, out HistorySyncOperation? operation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var entry) && entry.Stamp == stamp)
            {
                entry.LastUsedPass = _pass;
                operation = entry.Operation;
                return true;
            }
        }

        operation = null;
        return false;
    }

    internal void Set(string path, (long Length, DateTime LastWriteUtc) stamp, HistorySyncOperation? operation)
    {
        lock (_gate)
        {
            if (_entries.Count >= Capacity && !_entries.ContainsKey(path))
            {
                // Every existing file is looked up on every pass, so an entry untouched for a whole pass has no file.
                // One sweep per pass keeps a full cache from rescanning itself for every surplus file.
                if (_sweptPass != _pass)
                {
                    _sweptPass = _pass;
                    foreach (var stale in _entries.Where(pair => pair.Value.LastUsedPass < _pass - 1).Select(pair => pair.Key).ToArray())
                        _entries.Remove(stale);
                }
                if (_entries.Count >= Capacity) return;
            }

            _entries[path] = new Entry(stamp, operation) { LastUsedPass = _pass };
        }
    }

    private sealed class Entry((long Length, DateTime LastWriteUtc) stamp, HistorySyncOperation? operation)
    {
        public (long Length, DateTime LastWriteUtc) Stamp { get; } = stamp;
        public HistorySyncOperation? Operation { get; } = operation;
        public long LastUsedPass { get; set; }
    }
}
