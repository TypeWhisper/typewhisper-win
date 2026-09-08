namespace TypeWhisper.Presentation;

/// <summary>Outcome of an explicit copy request; no output is synthesized or loaded from History.</summary>
public enum LastDictationCopyResult
{
    /// <summary>The final text was written to the clipboard.</summary>
    Copied,
    /// <summary>No completed dictation is available in this session.</summary>
    Empty,
    /// <summary>An operation currently reserves capture or output.</summary>
    Busy,
    /// <summary>Shutdown or shortcut editing prevents invocation.</summary>
    Ignored,
    /// <summary>The clipboard write failed and may be explicitly retried.</summary>
    ClipboardUnavailable
}

/// <summary>Copies only a completed session snapshot, without touching the clipboard while input is reserved.</summary>
public static class LastDictationCopy
{
    /// <summary>Invokes the clipboard boundary once, only when a completed result is available and copying is allowed.</summary>
    public static LastDictationCopyResult Execute(LastCompletedDictation? snapshot, bool blocked, bool busy, Action<string> copy)
    {
        if (blocked) return LastDictationCopyResult.Ignored;
        if (busy) return LastDictationCopyResult.Busy;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text)) return LastDictationCopyResult.Empty;
        try { copy(snapshot.Text); return LastDictationCopyResult.Copied; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return LastDictationCopyResult.ClipboardUnavailable; }
    }
}
