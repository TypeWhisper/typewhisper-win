namespace TypeWhisper.Presentation;

/// <summary>Protects open workspaces from external navigation and file delivery.</summary>
public static class ActivationAdmission
{
    /// <summary>Allows navigation only from Quick Launch, or file requests into an idle visible file list.</summary>
    public static string? Reject(bool closing, bool workspaceOpen, bool filesOpen, bool filesAccepting, string? route)
    {
        if (closing) return "TypeWhisper is closing. Retry after reopening the app.";
        if (route is null) return null;
        if (!workspaceOpen) return null;
        if (route == "--files" && filesOpen && filesAccepting) return null;
        return "The activation was not applied. Your current workspace is unchanged. Return to Quick Launch, or finish the current file view, then retry.";
    }
}
