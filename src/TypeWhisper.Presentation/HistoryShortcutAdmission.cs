namespace TypeWhisper.Presentation;

/// <summary>Protects running operations and existing workspaces when History is requested externally.</summary>
public static class HistoryShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the existing History view can be focused or opened.</summary>
    public static string? Rejection(bool closing, bool operationBusy, bool otherWorkspaceOpen)
    {
        if (closing) return "History is unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening History. Your work is kept intact.";
        if (otherWorkspaceOpen) return "Return to Quick Launch before opening History. Your current workspace is kept intact.";
        return null;
    }
}
