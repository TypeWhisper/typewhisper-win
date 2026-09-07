using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>An immutable, explicitly supported automatic workflow selected at dictation start.</summary>
public sealed class AutomaticWorkflowSnapshot
{
    private readonly Workflow? _workflow;
    private AutomaticWorkflowSnapshot(Workflow? workflow, string? error)
    {
        // Keep no caller-owned collections. Only supported scalar prompt settings are copied.
        _workflow = workflow is null ? null : new Workflow
        {
            Id = workflow.Id, Name = workflow.Name, Template = workflow.Template, Trigger = WorkflowTrigger.Manual(),
            Behavior = new()
            {
                ProviderOverride = workflow.Behavior.ProviderOverride, ModelOverride = workflow.Behavior.ModelOverride,
                FineTuning = workflow.Behavior.FineTuning, TranslationTarget = workflow.Behavior.TranslationTarget
            }
        };
        Error = error;
    }

    /// <summary>The selected workflow identifier, or null for a catalog loading failure.</summary>
    public string? Id => _workflow?.Id;
    /// <summary>The selected workflow name.</summary>
    public string? Name => _workflow?.Name;
    /// <summary>A recoverable configuration error that prevents automatic insertion.</summary>
    public string? Error { get; }

    /// <summary>Creates a review-only result when the catalog cannot be read safely.</summary>
    public static AutomaticWorkflowSnapshot Unavailable() => new(null, "Workflows could not be loaded. Review your transcript; nothing was pasted.");

    /// <summary>Selects only App and Global rules; manual, website and hotkey triggers remain outside this slice.</summary>
    public static AutomaticWorkflowSnapshot? Select(IEnumerable<Workflow> workflows, string? processName)
    {
        var match = WorkflowService.MatchSnapshot(workflows.Where(w =>
            w.Trigger.Kind is WorkflowTriggerKind.App or WorkflowTriggerKind.Global), processName, null);
        if (match is null) return null;
        return new(match.Workflow, UnsupportedReason(match.Workflow));
    }

    /// <summary>Reports semantics that this automatic dictation integration cannot execute.</summary>
    public static string? UnsupportedReason(Workflow workflow)
    {
        var behavior = workflow.Behavior;
        var output = workflow.Output;
        if ((workflow.Trigger.Kind == WorkflowTriggerKind.Global && workflow.Trigger.HasAppBindings)
            || !Enum.IsDefined(workflow.Template) || workflow.Trigger.HasWebsiteBindings || workflow.Trigger.Hotkeys.Count != 0
            || behavior.Settings.Count != 0 || !string.IsNullOrWhiteSpace(behavior.InputLanguage)
            || behavior.InputLanguageHints.Count != 0 || !string.IsNullOrWhiteSpace(behavior.SelectedTask)
            || behavior.WhisperModeOverride is not null || !string.IsNullOrWhiteSpace(behavior.TranscriptionModelOverride)
            || !string.IsNullOrWhiteSpace(output.Format) || output.AutoEnter
            || !string.IsNullOrWhiteSpace(output.TargetActionPluginId) || !string.IsNullOrWhiteSpace(output.NumberNormalizationModeRaw))
            return "The selected workflow has unsupported trigger, recording or output settings. Review your transcript; nothing was pasted.";
        if (workflow.Template == WorkflowTemplate.Custom && string.IsNullOrWhiteSpace(behavior.FineTuning))
            return "The selected custom workflow requires instructions. Review your transcript; nothing was pasted.";
        return null;
    }

    /// <summary>Runs the exact provider and model with the Core prompt, rejecting late canceled or empty results.</summary>
    public async Task<string> ProcessAsync(string text, string? configuredLanguage, string? detectedLanguage,
        Func<string, string, bool> available,
        Func<string, string, string, string, CancellationToken, Task<string>> process,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Error is not null) throw new InvalidOperationException(Error);
        var workflow = _workflow!;
        var provider = workflow.Behavior.ProviderOverride;
        var model = workflow.Behavior.ModelOverride;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model) || !available(provider, model))
            throw new InvalidOperationException("The workflow provider or model is unavailable.");
        var prompt = workflow.SystemPrompt(detectedLanguage: detectedLanguage, configuredLanguage: configuredLanguage);
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("The workflow has no instructions.");
        var result = await process(provider, prompt, text, model, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("The workflow returned no text.");
        return result;
    }
}
