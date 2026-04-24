using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class RecentTranscriptionsPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<RecentTranscriptionPaletteItem> _allItems;
    private readonly Action<RecentTranscriptionPaletteItem> _onSelect;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private RecentTranscriptionPaletteItem? _selectedItem;

    public ObservableCollection<RecentTranscriptionPaletteItem> FilteredEntries { get; } = [];
    public bool HasFilteredEntries => FilteredEntries.Count > 0;

    public RecentTranscriptionsPaletteViewModel(
        IReadOnlyList<RecentTranscriptionEntry> entries,
        Action<RecentTranscriptionPaletteItem> onSelect)
    {
        _allItems = entries
            .Select(entry => new RecentTranscriptionPaletteItem(entry, BuildSubtitle(entry)))
            .ToList();
        _onSelect = onSelect;
        RefreshFilteredEntries();
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredEntries();

    public void MoveSelection(int offset)
    {
        if (FilteredEntries.Count == 0)
            return;

        var currentIndex = SelectedItem is null ? -1 : FilteredEntries.IndexOf(SelectedItem);
        var nextIndex = Math.Clamp(currentIndex + offset, 0, FilteredEntries.Count - 1);
        SelectedItem = FilteredEntries[nextIndex];
    }

    public void SelectCurrent() => Select(SelectedItem ?? FilteredEntries.FirstOrDefault());

    public void Select(RecentTranscriptionPaletteItem? item)
    {
        if (item is null)
            return;

        _onSelect(item);
    }

    private void RefreshFilteredEntries()
    {
        var query = SearchQuery.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(item => item.Matches(query)).ToList();

        FilteredEntries.Clear();
        foreach (var item in filtered)
            FilteredEntries.Add(item);

        SelectedItem = FilteredEntries.FirstOrDefault();
        OnPropertyChanged(nameof(HasFilteredEntries));
    }

    private static string BuildSubtitle(RecentTranscriptionEntry entry)
    {
        var appName = !string.IsNullOrWhiteSpace(entry.AppName)
            ? entry.AppName
            : entry.AppProcessName;
        var timestamp = entry.Timestamp.Kind == DateTimeKind.Utc
            ? entry.Timestamp.ToLocalTime()
            : entry.Timestamp;
        var time = timestamp.ToString("g");

        return string.IsNullOrWhiteSpace(appName)
            ? time
            : $"{appName} - {time}";
    }
}

public sealed record RecentTranscriptionPaletteItem(
    RecentTranscriptionEntry Entry,
    string Subtitle)
{
    public string FinalText => Entry.FinalText;

    public bool Matches(string query) =>
        FinalText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (Entry.AppName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Entry.AppProcessName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}
