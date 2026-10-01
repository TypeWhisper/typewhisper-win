using TypeWhisper.Core.Models;

namespace TypeWhisper.Presentation;

/// <summary>A workflow offered by the workflow palette.</summary>
/// <param name="Workflow">The saved workflow.</param>
/// <param name="Subtitle">Its template and how it is otherwise triggered, as on macOS.</param>
public sealed record WorkflowPaletteItem(Workflow Workflow, string Subtitle);

/// <summary>Chooses and filters what the workflow palette shows for selected text.</summary>
public static class WorkflowPalette
{
    /// <summary>
    /// Every enabled workflow that can run on text, whatever its trigger, as on macOS. Dictation-only workflows
    /// do not process text and are left out.
    /// </summary>
    public static IReadOnlyList<WorkflowPaletteItem> Candidates(IEnumerable<Workflow> workflows) => workflows
        .Where(workflow => workflow.IsEnabled && workflow.Template != WorkflowTemplate.Dictation && ManualWorkflowStore.IsEditable(workflow)
            && !ManualWorkflowStore.IsDictationShortcut(workflow))
        .OrderBy(workflow => workflow.SortOrder).ThenBy(workflow => workflow.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(workflow => new WorkflowPaletteItem(workflow, TemplateName(workflow.Template) + " · " + TriggerSummary(workflow.Trigger)))
        .ToArray();

    /// <summary>Filters by name and subtitle; an empty query keeps everything.</summary>
    public static IReadOnlyList<WorkflowPaletteItem> Filter(IReadOnlyList<WorkflowPaletteItem> items, string? query)
    {
        var term = query?.Trim() ?? "";
        return term.Length == 0 ? items : items.Where(item => item.Workflow.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || item.Subtitle.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    /// <summary>Moves a selection through a list, wrapping at both ends.</summary>
    public static int Move(int index, int offset, int count)
    {
        if (count <= 0) return -1;
        if (index < 0) return offset > 0 ? 0 : count - 1;
        return ((index + offset) % count + count) % count;
    }

    private static string TemplateName(WorkflowTemplate template) => template switch
    {
        WorkflowTemplate.CleanedText => Loc.T("Cleaned Text"),
        WorkflowTemplate.Translation => Loc.T("Translation"),
        WorkflowTemplate.EmailReply => Loc.T("Email Reply"),
        WorkflowTemplate.MeetingNotes => Loc.T("Meeting Notes"),
        WorkflowTemplate.Checklist => Loc.T("Checklist"),
        WorkflowTemplate.Json => "JSON",
        WorkflowTemplate.Summary => Loc.T("Summary"),
        _ => Loc.T("Custom")
    };

    private static string TriggerSummary(WorkflowTrigger trigger) => trigger.Kind switch
    {
        WorkflowTriggerKind.Manual => Loc.T("Manual"),
        WorkflowTriggerKind.Hotkey => trigger.Hotkeys.Count > 0 ? string.Join(", ", trigger.Hotkeys) : Loc.T("Shortcut"),
        WorkflowTriggerKind.App => trigger.ProcessNames.Count > 0 ? string.Join(", ", trigger.ProcessNames) : Loc.T("App"),
        WorkflowTriggerKind.Website => trigger.WebsitePatterns.Count > 0 ? string.Join(", ", trigger.WebsitePatterns) : Loc.T("Website"),
        _ => Loc.T("Always")
    };
}
