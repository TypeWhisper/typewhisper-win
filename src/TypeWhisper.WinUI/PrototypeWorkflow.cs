using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

public sealed record PrototypeWorkflow(string Id, string Title, string Description, string IconKind,
    string Instruction)
{
    public string ProviderId { get; init; } = "none";
    public string ModelId { get; init; } = "";
    public string OutputTarget { get; init; } = "preview";
    public bool IsEnabled { get; init; } = true;
    internal Workflow? Stored { get; init; }

    internal Workflow ToStored() => (Stored ?? new Workflow
    {
        Id = Id, Name = Title, Template = WorkflowTemplate.Custom, Trigger = WorkflowTrigger.Manual()
    }) with
    {
        Name = Title,
        IsEnabled = IsEnabled,
        Behavior = (Stored?.Behavior ?? new WorkflowBehavior()) with { FineTuning = Instruction, ProviderOverride = ProviderId, ModelOverride = ModelId }
    };

    internal static PrototypeWorkflow FromStored(Workflow workflow) => new(workflow.Id, workflow.Name,
        workflow.IsEnabled ? "Manual workflow" : "Disabled manual workflow", "workflow", workflow.Behavior.FineTuning)
    {
        ProviderId = workflow.Behavior.ProviderOverride ?? "none", ModelId = workflow.Behavior.ModelOverride ?? "", IsEnabled = workflow.IsEnabled, Stored = workflow
    };
}
