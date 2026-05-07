using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class WorkflowPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<WorkflowPaletteItem> _allItems;
    private readonly Action<WorkflowPaletteItem> _onSelect;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private WorkflowPaletteItem? _selectedItem;

    public string SourceTextPreview { get; }
    public ObservableCollection<WorkflowPaletteItem> FilteredWorkflows { get; } = [];
    public bool HasFilteredWorkflows => FilteredWorkflows.Count > 0;

    public WorkflowPaletteViewModel(
        IReadOnlyList<Workflow> workflows,
        string sourceText,
        Action<WorkflowPaletteItem> onSelect)
    {
        _allItems = workflows
            .Select(workflow => new WorkflowPaletteItem(
                workflow,
                workflow.Definition.Name,
                BuildSubtitle(workflow),
                WorkflowsViewModel.TemplateIconGlyph(workflow.Template)))
            .ToList();
        _onSelect = onSelect;
        SourceTextPreview = BuildSourceTextPreview(sourceText);
        RefreshFilteredWorkflows();
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredWorkflows();

    public void MoveSelection(int offset)
    {
        if (FilteredWorkflows.Count == 0)
            return;

        var currentIndex = SelectedItem is null ? -1 : FilteredWorkflows.IndexOf(SelectedItem);
        var nextIndex = Math.Clamp(currentIndex + offset, 0, FilteredWorkflows.Count - 1);
        SelectedItem = FilteredWorkflows[nextIndex];
    }

    public void SelectCurrent() => Select(SelectedItem ?? FilteredWorkflows.FirstOrDefault());

    public void Select(WorkflowPaletteItem? item)
    {
        if (item is null)
            return;

        _onSelect(item);
    }

    private void RefreshFilteredWorkflows()
    {
        var query = SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(item => item.Matches(query)).ToList();

        FilteredWorkflows.Clear();
        foreach (var item in filtered)
            FilteredWorkflows.Add(item);

        SelectedItem = FilteredWorkflows.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredWorkflows));
    }

    private static string BuildSubtitle(Workflow workflow)
    {
        if (!string.IsNullOrWhiteSpace(workflow.Output.TargetActionPluginId))
            return $"{workflow.Definition.Name} - {Loc.Instance["Workflows.ActionPlugin"]}";

        return workflow.Definition.Name;
    }

    private static string BuildSourceTextPreview(string sourceText)
    {
        var singleLine = string.Join(" ", sourceText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return singleLine.Length <= 220
            ? singleLine
            : singleLine[..217] + "...";
    }
}

public sealed record WorkflowPaletteItem(
    Workflow Workflow,
    string TemplateName,
    string Subtitle,
    string IconGlyph)
{
    public string Name => Workflow.Name;

    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || TemplateName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);
}
