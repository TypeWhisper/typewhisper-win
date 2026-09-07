using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

/// <summary>A manual workflow editor draft retaining the original Core metadata.</summary>
public sealed record PrototypeWorkflow(string Id, string Title, string Description, string IconKind,
    string Instruction)
{
    /// <summary>The exact selected LLM provider.</summary>
    public string ProviderId { get; init; } = "none";
    /// <summary>The exact selected provider model.</summary>
    public string ModelId { get; init; } = "";
    /// <summary>The manual result review destination.</summary>
    public string OutputTarget { get; init; } = "preview";
    /// <summary>Whether manual execution is enabled.</summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>The shared Core prompt template.</summary>
    public WorkflowTemplate Template { get; init; } = WorkflowTemplate.Custom;
    /// <summary>The translation language; null uses Core's English default.</summary>
    public string? TranslationTarget { get; init; }
    /// <summary>The explicit activation kind.</summary>
    public WorkflowTriggerKind TriggerKind { get; init; } = WorkflowTriggerKind.Manual;
    /// <summary>Comma-separated Windows process names for App activation.</summary>
    public string AppProcesses { get; init; } = "";
    /// <summary>Lower numbers win among equally specific rules.</summary>
    public int Priority { get; init; }
    internal Workflow? Stored { get; init; }
    internal string InstructionDescription => string.Join("\n", new[]
    {
        Template == WorkflowTemplate.Custom ? null : WorkflowTemplateCatalog.DefinitionFor(Template).Description,
        Template == WorkflowTemplate.Translation ? "Target language: " + (string.IsNullOrWhiteSpace(TranslationTarget) ? "English" : TranslationTarget) : null,
        string.IsNullOrWhiteSpace(Instruction) ? null : Instruction
    }.Where(text => text is not null));

    internal Workflow ToStored() => (Stored ?? new Workflow
    {
        Id = Id, Name = Title, Template = WorkflowTemplate.Custom, Trigger = WorkflowTrigger.Manual()
    }) with
    {
        Name = Title,
        Template = Template,
        IsEnabled = IsEnabled,
        SortOrder = Priority,
        Trigger = TriggerKind == WorkflowTriggerKind.Manual && Stored?.Trigger.Kind == WorkflowTriggerKind.Manual
            ? Stored.Trigger : TriggerKind switch
            {
                WorkflowTriggerKind.App => WorkflowTrigger.App(AppProcesses.Split(',', StringSplitOptions.RemoveEmptyEntries)),
                WorkflowTriggerKind.Global => WorkflowTrigger.Global(),
                _ => WorkflowTrigger.Manual()
            },
        Behavior = (Stored?.Behavior ?? new WorkflowBehavior()) with
        { FineTuning = Instruction, ProviderOverride = ProviderId, ModelOverride = ModelId, TranslationTarget = TranslationTarget }
    };

    internal static PrototypeWorkflow FromStored(Workflow workflow) => new(workflow.Id, workflow.Name,
        (workflow.IsEnabled ? "" : "Disabled Â· ") + workflow.Trigger.Kind + " · " + workflow.Definition.Name, "workflow", workflow.Behavior.FineTuning)
    {
        ProviderId = workflow.Behavior.ProviderOverride ?? "none", ModelId = workflow.Behavior.ModelOverride ?? "", IsEnabled = workflow.IsEnabled,
        TriggerKind = workflow.Trigger.Kind, AppProcesses = string.Join(", ", workflow.Trigger.ProcessNames), Priority = workflow.SortOrder,
        Template = workflow.Template, TranslationTarget = workflow.Behavior.TranslationTarget, Stored = workflow
    };
}
