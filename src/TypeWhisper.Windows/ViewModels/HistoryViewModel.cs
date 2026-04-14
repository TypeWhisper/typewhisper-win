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
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private bool _suppressRefresh;
    private bool _hasLoaded;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string? _selectedAppFilter;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];
    public ObservableCollection<string> AvailableApps { get; } = [];
    public ICollectionView GroupedEntries { get; }

    public int TotalRecords => _history.TotalRecords;
    public int TotalWords => _history.TotalWords;

    public HistoryViewModel(IHistoryService history, IDictionaryService dictionary)
    {
        _history = history;
        _dictionary = dictionary;
        Loc.Instance.LanguageChanged += OnLanguageChanged;

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
    }

    public async Task LoadAsync()
    {
        if (_hasLoaded) return;

        IsLoading = true;
        try
        {
            await _history.EnsureLoadedAsync().ConfigureAwait(false);
            await Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                _hasLoaded = true;
                RefreshRecords();
                OnPropertyChanged(nameof(TotalRecords));
                OnPropertyChanged(nameof(TotalWords));
            });
        }
        finally
        {
            Application.Current?.Dispatcher.Invoke(() => IsLoading = false);
        }
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
        foreach (var app in _history.GetDistinctApps())
            AvailableApps.Add(app);
        SelectedAppFilter = current is not null && AvailableApps.Contains(current) ? current : null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        var result = MessageBox.Show(
            Loc.Instance["History.ClearAllConfirm"],
            Loc.Instance["History.ClearAllTitle"],
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
            Filter = "Text (*.txt)|*.txt|CSV (*.csv)|*.csv|Markdown (*.md)|*.md|JSON (*.json)|*.json",
            DefaultExt = ".txt",
            FileName = Loc.Instance.GetString("History.ExportFilename", DateTime.Now)
        };
        if (dlg.ShowDialog() != true) return;

        var visibleRecords = Entries
            .Where(e => GroupedEntries.Filter?.Invoke(e) ?? true)
            .Select(e => e.Record)
            .ToList();

        var labels = new ExportLabels
        {
            Header = Loc.Instance["Export.Header"],
            Exported = Loc.Instance["Export.Exported"],
            Entries = Loc.Instance["Export.Entries"],
            Timestamp = Loc.Instance["Export.Timestamp"],
            App = Loc.Instance["Export.App"],
            Text = Loc.Instance["Export.Text"],
            Duration = Loc.Instance["Export.Duration"],
            Words = Loc.Instance["Export.Words"],
            Language = Loc.Instance["Export.Language"],
        };

        var content = dlg.FilterIndex switch
        {
            2 => _history.ExportToCsv(visibleRecords, labels),
            3 => _history.ExportToMarkdown(visibleRecords, labels),
            4 => _history.ExportToJson(visibleRecords),
            _ => _history.ExportToText(visibleRecords, labels)
        };

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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_hasLoaded)
                RefreshRecords();
        });
    }
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

        if (date == today) return Loc.Instance["History.Today"];
        if (date == today.AddDays(-1)) return Loc.Instance["History.Yesterday"];

        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisMonday = today.AddDays(-daysSinceMonday);
        if (date >= thisMonday) return Loc.Instance["History.ThisWeek"];

        var lastMonday = thisMonday.AddDays(-7);
        if (date >= lastMonday) return Loc.Instance["History.LastWeek"];

        return timestamp.ToString("MMMM yyyy");
    }
}
