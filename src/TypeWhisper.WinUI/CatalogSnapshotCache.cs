namespace TypeWhisper.WinUI;

// Building a recording snapshot parses the whole catalog and compiles one regex per correction, work that is the
// same for every dictation while the file stays the same. One built snapshot is kept per path and handed out again
// until the file's write time or length differs, or until a writer inside the app announces a change. Snapshots are
// immutable, so a dictation that already holds one is not affected by a later rebuild.
internal sealed class CatalogSnapshotCache<T> where T : class
{
    private readonly Func<string, T> _build;
    private readonly Func<T, bool> _isReusable;
    private readonly object _sync = new();
    private string? _path;
    private CatalogFileStamp _stamp;
    private T? _snapshot;
    private int _version;

    // Snapshots that isReusable rejects (a locked or corrupt file) are rebuilt on every request: a sync client
    // releasing the file does not change its stamp, so caching the failure would hide the recovery.
    internal CatalogSnapshotCache(Func<string, T> build, Func<T, bool> isReusable)
    {
        _build = build;
        _isReusable = isReusable;
    }

    internal T Get(string path)
    {
        // The stamp is read before the file, so a write that lands in between leaves a stamp the next request rejects.
        if (!CatalogFileStamp.TryRead(path, out var stamp)) return _build(path);
        int version;
        lock (_sync)
        {
            if (_snapshot is { } cached && _stamp == stamp && string.Equals(_path, path, StringComparison.Ordinal))
                return cached;
            version = _version;
        }
        var snapshot = _build(path);
        lock (_sync)
        {
            // A change announced during the build may carry the stamp just read; the next request rebuilds then.
            if (_version != version) return snapshot;
            _path = path;
            _stamp = stamp;
            _snapshot = _isReusable(snapshot) ? snapshot : null;
        }
        return snapshot;
    }

    // Writers inside the app call this because the stamp cannot tell apart a same-length rewrite that happens within
    // the file system's timestamp resolution of the previous write.
    internal void Invalidate()
    {
        lock (_sync)
        {
            _snapshot = null;
            _version++;
        }
    }
}

// Write time and length together reveal edits by sync clients or other processes without reading the file.
internal readonly record struct CatalogFileStamp(bool Exists, DateTime LastWriteUtc, long Length)
{
    internal static bool TryRead(string path, out CatalogFileStamp stamp)
    {
        try
        {
            // FileInfo fetches every attribute in one call; Exists is false for directories and unreadable paths,
            // which matches the missing-file behaviour of the builders.
            var info = new FileInfo(path);
            stamp = info.Exists ? new(true, info.LastWriteTimeUtc, info.Length) : default;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stamp = default;
            return false;
        }
    }
}
