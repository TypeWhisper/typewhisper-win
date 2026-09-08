namespace TypeWhisper.Presentation;

/// <summary>Protects running operations and existing workspaces when Recorder is requested externally.</summary>
public static class RecorderShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the existing recorder view can be focused or opened.</summary>
    public static string? Rejection(bool closing, bool operationBusy, bool otherWorkspaceOpen)
    {
        if (closing) return "The recorder is unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening the recorder. Your work is kept intact.";
        if (otherWorkspaceOpen) return "Return to Quick Launch before opening the recorder. Your current workspace is kept intact.";
        return null;
    }
}
