namespace TypeWhisper.Presentation;

/// <summary>Protects running operations when Recorder is requested externally.</summary>
public static class RecorderShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the existing recorder view can be focused or opened.</summary>
    public static string? Rejection(bool closing, bool operationBusy)
    {
        if (closing) return "The recorder is unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening the recorder. Your work is kept intact.";
        return null;
    }
}
