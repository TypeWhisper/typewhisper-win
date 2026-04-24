using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

public interface IWorkflowService
{
    IReadOnlyList<Workflow> Workflows { get; }
    event Action? WorkflowsChanged;

    void AddWorkflow(Workflow workflow);
    void UpdateWorkflow(Workflow workflow);
    void DeleteWorkflow(string id);
    void ToggleWorkflow(string id);
    void Reorder(IReadOnlyList<string> orderedIds);
    int NextSortOrder();
    Workflow? GetWorkflow(string id);
    WorkflowMatchResult? MatchWorkflow(string? processName, string? url);
    WorkflowMatchResult? ForceMatch(string workflowId);
}

public enum WorkflowMatchKind
{
    Website,
    App,
    ManualOverride
}

public sealed record WorkflowMatchResult(
    Workflow Workflow,
    WorkflowMatchKind Kind,
    string? MatchedDomain,
    int CompetingWorkflowCount,
    bool WonBySortOrder);
