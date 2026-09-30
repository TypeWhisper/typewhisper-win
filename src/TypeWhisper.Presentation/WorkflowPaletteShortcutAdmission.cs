namespace TypeWhisper.Presentation;

/// <summary>Protects running operations when WorkflowPalette is requested externally.</summary>
public static class WorkflowPaletteShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the existing workflow view can be focused or opened.</summary>
    public static string? Rejection(bool closing, bool operationBusy)
    {
        if (closing) return "The workflow palette is unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening the workflow palette. Your work is kept intact.";
        return null;
    }
}
