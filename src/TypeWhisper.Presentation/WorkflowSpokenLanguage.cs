namespace TypeWhisper.Presentation;

/// <summary>Resolves a recording's spoken language without changing the global preference.</summary>
public static class WorkflowSpokenLanguage
{
    /// <summary>Whether a stored value inherits the spoken language selected in Dictation.</summary>
    public static bool Inherits(string? language) =>
        string.IsNullOrWhiteSpace(language) || language is "global" or "inherit_global";

    /// <summary>Missing values inherit; otherwise automatic detection or a language code.</summary>
    public static bool IsSupported(string? language) => Inherits(language) || language == "auto"
        || language!.Length is >= 2 and <= 16 && char.IsAsciiLetterLower(language[0])
            && language.All(c => char.IsAsciiLetter(c) || c == '-');

    /// <summary>The language a matched workflow requests. An unsupported automatic rule applies none of its settings.</summary>
    public static string? SelectedLanguageFor(AutomaticWorkflowSnapshot? workflow) =>
        workflow is { Error: null } ? workflow.InputLanguage : null;

    /// <summary>Validates the effective language against the active model's choices before decoding.</summary>
    public static string Resolve(string? language, string globalLanguage, IReadOnlyList<string> choices)
    {
        if (Inherits(language)) return globalLanguage;
        if (!IsSupported(language)) throw new InvalidOperationException(Loc.T("This workflow has an unsupported spoken language."));
        if (language == "auto") return "auto";
        return choices.FirstOrDefault(code => code.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(Loc.T("The current transcription model does not support this workflow's spoken language. Choose another language in the workflow, or another model in Dictation."));
    }
}
