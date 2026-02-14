using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

public partial class DictionaryViewModel : ObservableObject
{
    private readonly IDictionaryService _dictionary;

    [ObservableProperty] private DictionaryEntry? _selectedEntry;
    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;

    public ObservableCollection<DictionaryEntry> Entries { get; } = [];

    public DictionaryViewModel(IDictionaryService dictionary)
    {
        _dictionary = dictionary;
        _dictionary.EntriesChanged += RefreshEntries;
        RefreshEntries();
    }

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewOriginal)) return;

        _dictionary.AddEntry(new DictionaryEntry
        {
            Id = Guid.NewGuid().ToString(),
            EntryType = NewEntryType,
            Original = NewOriginal.Trim(),
            Replacement = string.IsNullOrWhiteSpace(NewReplacement) ? null : NewReplacement.Trim()
        });

        NewOriginal = "";
        NewReplacement = "";
    }

    [RelayCommand]
    private void DeleteEntry()
    {
        if (SelectedEntry is null) return;
        _dictionary.DeleteEntry(SelectedEntry.Id);
        SelectedEntry = null;
    }

    [RelayCommand]
    private void ToggleEnabled()
    {
        if (SelectedEntry is null) return;
        _dictionary.UpdateEntry(SelectedEntry with { IsEnabled = !SelectedEntry.IsEnabled });
    }

    private void RefreshEntries()
    {
        Entries.Clear();
        foreach (var e in _dictionary.Entries)
            Entries.Add(e);
    }
}
