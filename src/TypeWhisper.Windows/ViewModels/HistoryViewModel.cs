using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;

    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private TranscriptionRecord? _selectedRecord;

    public ObservableCollection<TranscriptionRecord> Records { get; } = [];
    public int TotalRecords => _history.TotalRecords;
    public int TotalWords => _history.TotalWords;

    public HistoryViewModel(IHistoryService history)
    {
        _history = history;
        _history.RecordsChanged += () =>
        {
            RefreshRecords();
            OnPropertyChanged(nameof(TotalRecords));
            OnPropertyChanged(nameof(TotalWords));
        };
        RefreshRecords();
    }

    partial void OnSearchQueryChanged(string value) => RefreshRecords();

    [RelayCommand]
    private void RefreshRecords()
    {
        Records.Clear();
        var records = string.IsNullOrWhiteSpace(SearchQuery)
            ? _history.Records
            : _history.Search(SearchQuery);
        foreach (var r in records)
            Records.Add(r);
    }

    [RelayCommand]
    private void DeleteRecord()
    {
        if (SelectedRecord is null) return;
        _history.DeleteRecord(SelectedRecord.Id);
        SelectedRecord = null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        _history.ClearAll();
        SelectedRecord = null;
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (SelectedRecord is null) return;
        System.Windows.Clipboard.SetText(SelectedRecord.FinalText);
    }
}
