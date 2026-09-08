using System.Text.Json;
using TypeWhisper.Core.Models;

namespace TypeWhisper.WinUI;

public sealed partial class WorkflowsView
{
    private string? _apiConfigurationConflict;

    internal void RefreshApiData()
    {
        if (_closing || _store is null) return;
        try
        {
            var items = _store.Read();
            _workflows.Clear();
            _workflows.AddRange(items.Select(WorkflowDraft.FromStored));
            _loadError = null;
            if (_page == Page.Configuration)
            {
                _apiConfigurationConflict = ApiWorkflowConflict(_opened, items);
                UpdateConfigurationState();
            }
            else if (_page == Page.List) Filter(_query);
            else if (_opened is not null)
            {
                var latest = _workflows.FirstOrDefault(workflow => workflow.Id == _opened.Id);
                if (latest is not null)
                {
                    _opened = latest;
                    WorkflowInstruction.Text = latest.InstructionDescription;
                    UpdateSourceState();
                }
                else
                {
                    WorkflowPrimaryButton.IsEnabled = false;
                    WorkflowInputHint.Text = "This workflow was removed through the API. Your source text is still here.";
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadError = "Workflows could not be reloaded. Your open draft has been kept.";
            if (_page == Page.Configuration)
            {
                _apiConfigurationConflict = _loadError;
                UpdateConfigurationState();
            }
            else WorkflowInputHint.Text = _loadError;
        }
    }

    private static string? ApiWorkflowConflict(WorkflowDraft? draft, IReadOnlyList<Workflow> items)
    {
        if (draft is null) return null;
        var current = items.FirstOrDefault(item => item.Id == draft.Id);
        // Save normalizes UpdatedAt; it is not an editable workflow setting.
        var before = draft.Stored is null ? null : JsonSerializer.Serialize(draft.Stored with { UpdatedAt = default });
        var after = current is null ? null : JsonSerializer.Serialize(current with { UpdatedAt = default });
        return before == after ? null
            : "This workflow changed through the API. Your draft is still here; copy any changes you need, then reopen the workflow before saving.";
    }

    private void RequireUnchangedApiWorkflow(WorkflowDraft draft)
    {
        if (_store is null) throw new InvalidOperationException("Workflow storage is unavailable.");
        if (ApiWorkflowConflict(draft, _store.Read()) is { } error)
        {
            _apiConfigurationConflict = error;
            throw new InvalidOperationException(error);
        }
    }
}
