namespace TypeWhisper.Presentation;

/// <summary>Resolves a recording's spoken language without changing the global preference.</summary>
public static class WorkflowSpokenLanguage
{
    /// <summary>Whether a stored value inherits the spoken language selected in Dictation.</summary>
    public static bool Inherits(string? language) => string.IsNullOrWhiteSpace(language)
        || language.Equals("global", StringComparison.OrdinalIgnoreCase) || language.Equals("inherit_global", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutomatic(string? language) => string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>Missing values inherit; otherwise automatic detection or a provider language identifier such as ar_en.</summary>
    public static bool IsSupported(string? language) => Inherits(language) || IsAutomatic(language)
        || language!.Length is >= 2 and <= 32 && char.IsAsciiLetter(language[0])
            && language.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The language a matched workflow requests. An unsupported automatic rule applies none of its settings.</summary>
    public static string? SelectedLanguageFor(AutomaticWorkflowSnapshot? workflow) =>
        workflow is { Error: null } ? workflow.InputLanguage : null;

    /// <summary>Validates the effective language against the active model before decoding.</summary>
    /// <param name="language">The workflow's stored language.</param>
    /// <param name="globalLanguage">The language selected in Dictation.</param>
    /// <param name="choices">The languages the active model accepts.</param>
    /// <param name="detectsLanguage">Whether the active model offers automatic detection.</param>
    public static string Resolve(string? language, string globalLanguage, IReadOnlyList<string> choices, bool detectsLanguage)
    {
        if (Inherits(language)) return globalLanguage;
        if (!IsSupported(language)) throw new InvalidOperationException(Loc.T("This workflow has an unsupported spoken language."));
        if (IsAutomatic(language))
            return detectsLanguage ? "auto"
                : throw new NotSupportedException(Loc.T("The current transcription model cannot detect the language automatically. Choose a language in the workflow."));
        return choices.FirstOrDefault(code => code.Equals(language, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(Loc.T("The current transcription model does not support this workflow's spoken language. Choose another language in the workflow, or another model in Dictation."));
    }
}
