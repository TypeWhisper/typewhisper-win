using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private bool _suppressRefresh;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string? _selectedAppFilter;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];
    public ObservableCollection<string> AvailableApps { get; } = [];
    public ICollectionView GroupedEntries { get; }

    public int TotalRecords => _history.TotalRecords;
    public int TotalWords => _history.TotalWords;

    public HistoryViewModel(IHistoryService history, IDictionaryService dictionary)
    {
        _history = history;
        _dictionary = dictionary;

        GroupedEntries = CollectionViewSource.GetDefaultView(Entries);
        GroupedEntries.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(HistoryEntryViewModel.DateGroup)));
        GroupedEntries.Filter = FilterEntry;

        _history.RecordsChanged += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_suppressRefresh) return;
                RefreshRecords();
                OnPropertyChanged(nameof(TotalRecords));
                OnPropertyChanged(nameof(TotalWords));
            });
        };
        RefreshRecords();
    }

    partial void OnSearchQueryChanged(string value) => GroupedEntries.Refresh();
    partial void OnSelectedAppFilterChanged(string? value) => GroupedEntries.Refresh();

    private bool FilterEntry(object obj)
    {
        if (obj is not HistoryEntryViewModel entry) return false;

        if (!string.IsNullOrWhiteSpace(SelectedAppFilter) &&
            !string.Equals(entry.Record.AppProcessName, SelectedAppFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        var q = SearchQuery;
        return entry.Record.FinalText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               entry.Record.RawText.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (entry.Record.AppName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    [RelayCommand]
    private void RefreshRecords()
    {
        Entries.Clear();
        foreach (var r in _history.Records)
            Entries.Add(new HistoryEntryViewModel(r, this));

        RebuildAppFilter();
        GroupedEntries.Refresh();
    }

    private void RebuildAppFilter()
    {
        var current = SelectedAppFilter;
        AvailableApps.Clear();
        foreach (var app in _history.Records
                     .Select(r => r.AppProcessName)
                     .Where(a => !string.IsNullOrEmpty(a))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order())
            AvailableApps.Add(app!);
        SelectedAppFilter = current is not null && AvailableApps.Contains(current) ? current : null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        var result = MessageBox.Show(
            "Alle Verlaufseinträge unwiderruflich löschen?",
            "Verlauf löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
            _history.ClearAll();
    }

    [RelayCommand]
    private void ClearAppFilter() => SelectedAppFilter = null;

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt|CSV (*.csv)|*.csv",
            DefaultExt = ".txt",
            FileName = $"TypeWhisper-Verlauf-{DateTime.Now:yyyy-MM-dd}"
        };
        if (dlg.ShowDialog() != true) return;

        var visibleRecords = Entries
            .Where(e => GroupedEntries.Filter?.Invoke(e) ?? true)
            .Select(e => e.Record)
            .ToList();

        var content = dlg.FilterIndex == 2
            ? _history.ExportToCsv(visibleRecords)
            : _history.ExportToText(visibleRecords);

        File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);
    }

    internal void CollapseAllExcept(HistoryEntryViewModel? keep)
    {
        foreach (var entry in Entries)
            if (entry != keep && entry.IsExpanded)
            {
                entry.IsEditing = false;
                entry.IsExpanded = false;
            }
    }

    internal void DeleteEntry(HistoryEntryViewModel entry) =>
        _history.DeleteRecord(entry.Record.Id);

    internal void SaveEdit(HistoryEntryViewModel entry, string newText)
    {
        var originalText = entry.Record.FinalText;

        // Suppress refresh so RecordsChanged doesn't rebuild entries and lose suggestions
        _suppressRefresh = true;
        try
        {
            _history.UpdateRecord(entry.Record.Id, newText);
        }
        finally
        {
            _suppressRefresh = false;
        }

        // Extract correction suggestions
        var suggestions = TextDiffService.ExtractCorrections(originalText, newText);
        if (suggestions.Count > 0)
        {
            entry.CorrectionSuggestions.Clear();
            foreach (var s in suggestions)
                entry.CorrectionSuggestions.Add(s);
            entry.HasSuggestions = true;
        }

        OnPropertyChanged(nameof(TotalRecords));
        OnPropertyChanged(nameof(TotalWords));
    }

    internal void LearnCorrection(CorrectionSuggestion suggestion) =>
        _dictionary.LearnCorrection(suggestion.Original, suggestion.Replacement);
}

public partial class HistoryEntryViewModel : ObservableObject
{
    private readonly HistoryViewModel _parent;

    public TranscriptionRecord Record { get; private set; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private bool _hasSuggestions;

    public ObservableCollection<CorrectionSuggestion> CorrectionSuggestions { get; } = [];

    public string DateGroup => ComputeDateGroup(Record.Timestamp);
    public string TimeLabel => Record.Timestamp.ToString("HH:mm");
    public string DurationLabel => $"{Record.DurationSeconds:F1}s";

    public HistoryEntryViewModel(TranscriptionRecord record, HistoryViewModel parent)
    {
        Record = record;
        _parent = parent;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
            _parent.CollapseAllExcept(this);
        else
        {
            IsEditing = false;
            HasSuggestions = false;
            CorrectionSuggestions.Clear();
        }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void StartEdit()
    {
        EditText = Record.FinalText;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        _parent.SaveEdit(this, EditText);
        Record = Record with { FinalText = EditText };
        IsEditing = false;
        OnPropertyChanged(nameof(Record));
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private void Copy() => Clipboard.SetText(Record.FinalText);

    [RelayCommand]
    private void Delete() => _parent.DeleteEntry(this);

    [RelayCommand]
    private void AcceptSuggestions()
    {
        foreach (var s in CorrectionSuggestions)
            _parent.LearnCorrection(s);
        HasSuggestions = false;
        CorrectionSuggestions.Clear();
    }

    [RelayCommand]
    private void DismissSuggestions()
    {
        HasSuggestions = false;
        CorrectionSuggestions.Clear();
    }

    private static string ComputeDateGroup(DateTime timestamp)
    {
        var today = DateTime.Today;
        var date = timestamp.Date;

        if (date == today) return "Heute";
        if (date == today.AddDays(-1)) return "Gestern";

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday) return "Diese Woche";

        var lastMonday = thisMonday.AddDays(-7);
        if (date >= lastMonday) return "Letzte Woche";

        return timestamp.ToString("MMMM yyyy");
    }
}
