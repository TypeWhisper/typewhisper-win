using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.ViewModels;

public partial class DictionaryViewModel : ObservableObject
{
    private readonly IDictionaryService _dictionary;
    private readonly ISettingsService _settings;

    // Tab: 0=Alle, 1=Begriffe, 2=Korrekturen, 3=Packs
    [ObservableProperty] private int _selectedTab;

    // Add form
    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;
    [ObservableProperty] private bool _newCaseSensitive;

    // Edit modal
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private DictionaryEntry? _editEntry;
    [ObservableProperty] private string _editOriginal = "";
    [ObservableProperty] private string _editReplacement = "";
    [ObservableProperty] private bool _editCaseSensitive;

    public ObservableCollection<DictionaryEntry> Entries { get; } = [];
    public ICollectionView FilteredEntries { get; }
    public ObservableCollection<TermPackViewModel> Packs { get; } = [];

    public DictionaryViewModel(IDictionaryService dictionary, ISettingsService settings)
    {
        _dictionary = dictionary;
        _settings = settings;

        FilteredEntries = CollectionViewSource.GetDefaultView(Entries);
        FilteredEntries.Filter = FilterByTab;

        _dictionary.EntriesChanged += RefreshEntries;
        RefreshEntries();
        InitializePacks();
    }

    partial void OnSelectedTabChanged(int value) => FilteredEntries.Refresh();

    private bool FilterByTab(object obj)
    {
        if (obj is not DictionaryEntry entry) return false;
        return SelectedTab switch
        {
            1 => entry.EntryType == DictionaryEntryType.Term,
            2 => entry.EntryType == DictionaryEntryType.Correction,
            _ => true // 0=Alle, 3=Packs (entries hidden, packs shown)
        };
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
            Replacement = string.IsNullOrWhiteSpace(NewReplacement) ? null : NewReplacement.Trim(),
            CaseSensitive = NewCaseSensitive
        });

        NewOriginal = "";
        NewReplacement = "";
        NewCaseSensitive = false;
    }

    [RelayCommand]
    private void DeleteEntry(DictionaryEntry? entry)
    {
        if (entry is null) return;
        _dictionary.DeleteEntry(entry.Id);
    }

    [RelayCommand]
    private void ToggleEnabled(DictionaryEntry? entry)
    {
        if (entry is null) return;
        _dictionary.UpdateEntry(entry with { IsEnabled = !entry.IsEnabled });
    }

    [RelayCommand]
    private void StartEdit(DictionaryEntry? entry)
    {
        if (entry is null) return;
        EditEntry = entry;
        EditOriginal = entry.Original;
        EditReplacement = entry.Replacement ?? "";
        EditCaseSensitive = entry.CaseSensitive;
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (EditEntry is null || string.IsNullOrWhiteSpace(EditOriginal)) return;

        _dictionary.UpdateEntry(EditEntry with
        {
            Original = EditOriginal.Trim(),
            Replacement = string.IsNullOrWhiteSpace(EditReplacement) ? null : EditReplacement.Trim(),
            CaseSensitive = EditCaseSensitive
        });

        IsEditing = false;
        EditEntry = null;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditEntry = null;
    }

    // Pack management
    [RelayCommand]
    private void TogglePack(TermPackViewModel? pack)
    {
        if (pack is null) return;

        if (pack.IsEnabled)
        {
            _dictionary.DeactivatePack(pack.Pack.Id);
            pack.IsEnabled = false;
            SavePackState();
        }
        else
        {
            _dictionary.ActivatePack(pack.Pack);
            pack.IsEnabled = true;
            SavePackState();
        }
    }

    private void SavePackState()
    {
        var enabledIds = Packs.Where(p => p.IsEnabled).Select(p => p.Pack.Id).ToArray();
        _settings.Save(_settings.Current with { EnabledPackIds = enabledIds });
    }

    private void InitializePacks()
    {
        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet();
        foreach (var pack in TermPack.AllPacks)
        {
            Packs.Add(new TermPackViewModel(pack, enabledIds.Contains(pack.Id)));
        }
    }

    private void RefreshEntries()
    {
        Entries.Clear();
        foreach (var e in _dictionary.Entries)
            Entries.Add(e);
        FilteredEntries.Refresh();
    }
}

public partial class TermPackViewModel : ObservableObject
{
    public TermPack Pack { get; }
    [ObservableProperty] private bool _isEnabled;

    public string TermsPreview => string.Join(", ", Pack.Terms.Take(8)) +
        (Pack.Terms.Length > 8 ? $" +{Pack.Terms.Length - 8}" : "");

    public TermPackViewModel(TermPack pack, bool isEnabled)
    {
        Pack = pack;
        _isEnabled = isEnabled;
    }
}
