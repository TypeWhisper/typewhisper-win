using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>The translation a plugin asks for when it takes over a dictation into its app, as a workflow.</summary>
public static class DictationTargetTranslation
{
    private const int MaxLanguageLength = 64;

    /// <summary>
    /// The translation into <paramref name="language"/> as an App workflow for <paramref name="processName"/>, or null
    /// when the language is missing or not a plain name. It ranks last among the app's App rules, so a workflow of the
    /// user's own for the app still wins, while it beats a Global one. The shared workflow LLM translates.
    /// </summary>
    public static Workflow? Workflow(string processName, string appName, string? language)
    {
        if (language?.Trim() is not { Length: > 0 and <= MaxLanguageLength } target
            || !target.All(c => char.IsLetter(c) || c is ' ' or '-')) return null;
        return new Workflow
        {
            Id = "plugin-translation", Name = Loc.T("Translation for {0}", appName), Template = WorkflowTemplate.Translation,
            SortOrder = int.MaxValue, Trigger = WorkflowTrigger.App([processName]),
            Behavior = new() { TranslationTarget = target, ProviderOverride = WorkflowLlmDefaults.Inherit }
        };
    }
}
