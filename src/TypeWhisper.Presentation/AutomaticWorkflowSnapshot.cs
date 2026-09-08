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

    /// <summary>Captures an explicit shortcut independently of app and global matching.</summary>
    public static AutomaticWorkflowSnapshot ForDictationShortcut(Workflow workflow)
    {
        if (!workflow.IsEnabled || !ManualWorkflowStore.IsDictationShortcut(workflow))
            throw new InvalidOperationException("This dictation workflow is disabled or unsupported.");
        return new(workflow, null);
    }

    /// <summary>Captures an explicitly requested API workflow after validating its supported semantics.</summary>
    public static AutomaticWorkflowSnapshot ForApi(Workflow workflow)
    {
        if (!workflow.IsEnabled) throw new InvalidOperationException("This workflow is disabled.");
        var error = UnsupportedReason(workflow);
        if (error is not null) throw new InvalidOperationException(error);
        return new(workflow, null);
    }

    /// <summary>Selects App, Website and Global rules using a one-time browser hostname; manual and hotkey rules remain separate.</summary>
    public static AutomaticWorkflowSnapshot? Select(IEnumerable<Workflow> workflows, string? processName, string? browserHost = null,
        Func<Workflow, Workflow>? resolve = null)
    {
        var match = WorkflowService.MatchSnapshot(workflows.Where(w =>
            w.Trigger.Kind is WorkflowTriggerKind.App or WorkflowTriggerKind.Website or WorkflowTriggerKind.Global)
            .Select(workflow => workflow with { Trigger = workflow.Trigger with
            { WebsitePatterns = workflow.Trigger.WebsitePatterns.Select(pattern => BrowserWorkflowContext.NormalizePattern(pattern) ?? pattern).ToArray() } }),
            processName, BrowserWorkflowContext.NormalizeHost(browserHost));
        if (match is null) return null;
        var selected = resolve?.Invoke(match.Workflow) ?? match.Workflow;
        return new(selected, UnsupportedReason(selected));
    }

    /// <summary>Reports semantics that this automatic dictation integration cannot execute.</summary>
    public static string? UnsupportedReason(Workflow workflow)
    {
        var behavior = workflow.Behavior;
        var output = workflow.Output;
        if ((workflow.Trigger.Kind == WorkflowTriggerKind.Global && (workflow.Trigger.HasAppBindings || workflow.Trigger.HasWebsiteBindings))
            || !Enum.IsDefined(workflow.Template) || !Enum.IsDefined(workflow.Trigger.ContextMatchMode)
            || workflow.Trigger.WebsitePatterns.Any(pattern => BrowserWorkflowContext.NormalizePattern(pattern) is null)
            || workflow.Trigger.Hotkeys.Count != 0
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
