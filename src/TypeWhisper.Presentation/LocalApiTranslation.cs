using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>Captures the Windows translation provider and instructions for one API request.</summary>
public static class LocalApiTranslation
{
    /// <summary>Validates the default LLM before audio processing and builds the shared translation prompt.</summary>
    public static (string Provider, string Model, string Prompt) Prepare(string target,
        WorkflowLlmSelection? selection, Func<string, string, bool> available)
    {
        if (selection is null || !available(selection.Provider, selection.Model))
            throw new LocalApiRequestException(422, "Choose an available default LLM in Workflows to use target_language on Windows.");
        var prompt = new Workflow
        {
            Id = "api-translation", Name = "API translation", Trigger = WorkflowTrigger.Manual(),
            Template = WorkflowTemplate.Translation,
            Behavior = new() { TranslationTarget = target, ProviderOverride = selection.Provider, ModelOverride = selection.Model }
        }.SystemPrompt() ?? throw new LocalApiRequestException(422, "Translation instructions are unavailable.");
        return (selection.Provider, selection.Model, prompt);
    }
}
