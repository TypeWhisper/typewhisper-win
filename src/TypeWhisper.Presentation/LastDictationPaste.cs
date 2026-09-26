namespace TypeWhisper.Presentation;

/// <summary>Outcome of an explicit request to insert the last completed dictation again.</summary>
public enum LastDictationPasteResult
{
    /// <summary>The final text was pasted into the target window.</summary>
    Pasted,
    /// <summary>No completed dictation is available in this session.</summary>
    Empty,
    /// <summary>No window outside TypeWhisper is known to receive the text.</summary>
    NoTarget,
    /// <summary>An operation currently reserves capture or output.</summary>
    Busy,
    /// <summary>Shutdown or shortcut editing prevents invocation.</summary>
    Ignored,
    /// <summary>The target changed, kept modifier keys held, or the clipboard was unavailable.</summary>
    NotPasted
}

/// <summary>Pastes only a completed session snapshot, and only while no capture or output is reserved.</summary>
public static class LastDictationPaste
{
    /// <summary>Invokes the paste boundary at most once, only when a completed result and a target are available.</summary>
    public static async Task<LastDictationPasteResult> RunAsync(LastCompletedDictation? snapshot, bool blocked, bool busy,
        bool hasTarget, Func<string, Task<bool>> paste)
    {
        if (blocked) return LastDictationPasteResult.Ignored;
        if (busy) return LastDictationPasteResult.Busy;
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text)) return LastDictationPasteResult.Empty;
        if (!hasTarget) return LastDictationPasteResult.NoTarget;
        try { return await paste(snapshot.Text) ? LastDictationPasteResult.Pasted : LastDictationPasteResult.NotPasted; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return LastDictationPasteResult.NotPasted; }
    }
}

/// <summary>Decides which foreground windows may receive text pasted from the tray.</summary>
public static class PasteTargetFilter
{
    // Taskbar, tray overflow, desktop and menu windows gain the foreground while the tray menu opens.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow", "Progman", "WorkerW", "#32768"
    };

    /// <summary>Whether a foreground window belongs to another app and can hold a text field.</summary>
    public static bool IsEligible(string? className, uint processId, uint ownProcessId) =>
        processId != 0 && processId != ownProcessId && !string.IsNullOrEmpty(className) && !ShellClasses.Contains(className);
}
