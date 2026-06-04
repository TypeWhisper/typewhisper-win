using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides workflow palette view model behavior.
/// </summary>
public partial class WorkflowPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<WorkflowPaletteItem> _allItems;
    private readonly Action<WorkflowPaletteItem> _onSelect;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private WorkflowPaletteItem? _selectedItem;

    /// <summary>
    /// Gets the source text preview.
    /// </summary>
    public string SourceTextPreview { get; }
    /// <summary>
    /// Gets the filtered workflows.
    /// </summary>
    public ObservableCollection<WorkflowPaletteItem> FilteredWorkflows { get; } = [];
    /// <summary>
    /// Gets whether has filtered workflows.
    /// </summary>
    public bool HasFilteredWorkflows => FilteredWorkflows.Count > 0;

    /// <summary>
    /// Initializes a new instance of the WorkflowPaletteViewModel class.
    /// </summary>
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

    /// <summary>
    /// Moves selection.
    /// </summary>
    public void MoveSelection(int offset)
    {
        if (FilteredWorkflows.Count == 0)
            return;

        var currentIndex = SelectedItem is null ? -1 : FilteredWorkflows.IndexOf(SelectedItem);
        var nextIndex = Math.Clamp(currentIndex + offset, 0, FilteredWorkflows.Count - 1);
        SelectedItem = FilteredWorkflows[nextIndex];
    }

    /// <summary>
    /// Selects current.
    /// </summary>
    public void SelectCurrent() => Select(SelectedItem ?? FilteredWorkflows.FirstOrDefault());

    /// <summary>
    /// Performs select.
    /// </summary>
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

/// <summary>
/// Represents workflow palette item data.
/// </summary>
/// <param name="Workflow">Workflow supplied to the member.</param>
/// <param name="TemplateName">Template name supplied to the member.</param>
/// <param name="Subtitle">Subtitle supplied to the member.</param>
/// <param name="IconGlyph">Icon glyph supplied to the member.</param>
public sealed record WorkflowPaletteItem(
    Workflow Workflow,
    string TemplateName,
    string Subtitle,
    string IconGlyph)
{
    /// <summary>
    /// Gets the display or storage name.
    /// </summary>
    public string Name => Workflow.Name;

    /// <summary>
    /// Performs matches.
    /// </summary>
    public bool Matches(string query) =>
        Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || TemplateName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase);
}
