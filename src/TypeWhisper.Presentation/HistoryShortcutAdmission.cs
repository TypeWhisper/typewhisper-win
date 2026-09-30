namespace TypeWhisper.Presentation;

/// <summary>Protects running operations when recent transcriptions are requested by shortcut.</summary>
public static class HistoryShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the recent-transcriptions palette can open.</summary>
    public static string? Rejection(bool closing, bool operationBusy)
    {
        if (closing) return "Recent transcriptions are unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening recent transcriptions. Your work is kept intact.";
        return null;
    }
}
