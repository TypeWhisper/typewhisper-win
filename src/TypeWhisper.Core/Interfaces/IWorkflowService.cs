using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the workflow service contract.
/// </summary>
public interface IWorkflowService
{
    /// <summary>
    /// Gets the configured workflows in display order.
    /// </summary>
    IReadOnlyList<Workflow> Workflows { get; }
    /// <summary>
    /// Raised when workflows changes.
    /// </summary>
    event Action? WorkflowsChanged;

    /// <summary>
    /// Adds workflow.
    /// </summary>
    void AddWorkflow(Workflow workflow);
    /// <summary>
    /// Updates workflow.
    /// </summary>
    void UpdateWorkflow(Workflow workflow);
    /// <summary>
    /// Deletes workflow.
    /// </summary>
    void DeleteWorkflow(string id);
    /// <summary>
    /// Toggles workflow.
    /// </summary>
    void ToggleWorkflow(string id);
    /// <summary>
    /// Reorders
    /// </summary>
    void Reorder(IReadOnlyList<string> orderedIds);
    /// <summary>
    /// Performs next sort order.
    /// </summary>
    int NextSortOrder();
    /// <summary>
    /// Returns workflow.
    /// </summary>
    Workflow? GetWorkflow(string id);
    /// <summary>
    /// Performs match workflow.
    /// </summary>
    WorkflowMatchResult? MatchWorkflow(string? processName, string? url);
    /// <summary>
    /// Performs force match.
    /// </summary>
    WorkflowMatchResult? ForceMatch(string workflowId);
}

/// <summary>
/// Lists the supported workflow match kind values.
/// </summary>
public enum WorkflowMatchKind
{
    /// <summary>
    /// Represents the app and website option.
    /// </summary>
    AppAndWebsite,
    /// <summary>
    /// Represents the website option.
    /// </summary>
    Website,
    /// <summary>
    /// Represents the app option.
    /// </summary>
    App,
    /// <summary>
    /// Represents the global fallback option.
    /// </summary>
    GlobalFallback,
    /// <summary>
    /// Represents the manual override option.
    /// </summary>
    ManualOverride
}

/// <summary>
/// Represents workflow match result data.
/// </summary>
/// <param name="Workflow">Workflow supplied to the member.</param>
/// <param name="Kind">Kind supplied to the member.</param>
/// <param name="MatchedDomain">Matched domain supplied to the member.</param>
/// <param name="CompetingWorkflowCount">Competing workflow count supplied to the member.</param>
/// <param name="WonBySortOrder">Won by sort order supplied to the member.</param>
public sealed record WorkflowMatchResult(
    Workflow Workflow,
    WorkflowMatchKind Kind,
    string? MatchedDomain,
    int CompetingWorkflowCount,
    bool WonBySortOrder);
