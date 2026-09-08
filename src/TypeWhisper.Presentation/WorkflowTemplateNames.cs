namespace TypeWhisper.Presentation;

/// <summary>Updates suggested names while preserving names chosen by the user.</summary>
public static class WorkflowTemplateNames
{
    /// <summary>Replaces an empty or still automatically suggested name.</summary>
    public static string ForSelection(string current, string? previousSuggestion, string nextSuggestion) =>
        string.IsNullOrWhiteSpace(current) || current == previousSuggestion ? nextSuggestion : current;
}
