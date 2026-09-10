namespace TypeWhisper.Presentation;

/// <summary>Protects running operations and existing workspaces when WorkflowPalette is requested externally.</summary>
public static class WorkflowPaletteShortcutAdmission
{
    /// <summary>Returns a visible refusal, or null when the existing workflow view can be focused or opened.</summary>
    public static string? Rejection(bool closing, bool operationBusy, bool otherWorkspaceOpen)
    {
        if (closing) return "The workflow palette is unavailable while the app closes or restores a profile.";
        if (operationBusy) return "Finish the current operation before opening the workflow palette. Your work is kept intact.";
        if (otherWorkspaceOpen) return "Return to Quick Launch before opening the workflow palette. Your current workspace is kept intact.";
        return null;
    }
}
