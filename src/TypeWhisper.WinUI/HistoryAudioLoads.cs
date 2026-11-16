namespace TypeWhisper.WinUI;

// Which audio load of the History window is current, and the time to set once its media has opened. A newer load, or
// stopping playback, makes every earlier load stale, so a load that finishes late neither installs its player nor
// applies its time. Times asked for while a record's audio loads replace each other; only the latest is applied.
// UI thread only.
internal sealed class HistoryAudioLoads
{
    private int _generation;

    // The record whose audio is loading and has not opened yet, or null.
    internal string? LoadingId { get; private set; }
    internal TimeSpan? PendingPosition { get; private set; }

    // Starts a load and returns its token; every earlier load becomes stale.
    internal int Begin(string id, TimeSpan? position)
    {
        LoadingId = id;
        PendingPosition = position;
        return ++_generation;
    }

    internal bool IsCurrent(int load) => load == _generation;

    // While this record's audio loads, the time replaces the pending one. False when no load of this record runs.
    internal bool TrySeekPending(string id, TimeSpan position)
    {
        if (LoadingId != id) return false;
        PendingPosition = position;
        return true;
    }

    // The load opened its media: the time to set now, once, or null.
    internal TimeSpan? Opened(int load)
    {
        if (!IsCurrent(load) || LoadingId is null) return null;
        var position = PendingPosition;
        LoadingId = null;
        PendingPosition = null;
        return position;
    }

    // Playback stopped or another record opened: every load so far is stale and no time is pending.
    internal void Reset()
    {
        _generation++;
        LoadingId = null;
        PendingPosition = null;
    }
}
