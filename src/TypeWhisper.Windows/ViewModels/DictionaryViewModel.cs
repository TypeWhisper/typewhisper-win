using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public sealed record LocalizedIndustryPresetOption(string Id, string DisplayName, string? TermPackId);

public partial class DictionaryViewModel : ObservableObject
{
    private readonly IDictionaryService _dictionary;
    private readonly ISettingsService _settings;
    private readonly LicenseService? _license;
    private readonly TermPackRegistryService? _termPackRegistry;
    private IReadOnlyList<TermPack> _remotePacks = [];

    // Tab: 0=Alle, 1=Begriffe, 2=Korrekturen, 3=Packs
    [ObservableProperty] private int _selectedTab;

    // Search
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _vocabularyBoostingEnabled;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    // Add form
    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;
    [ObservableProperty] private bool _newCaseSensitive;

    // Segmented button helpers
    public bool IsNewTypeCorrection
    {
        get => NewEntryType == DictionaryEntryType.Correction;
        set { if (value) NewEntryType = DictionaryEntryType.Correction; }
    }

    public bool IsNewTypeTerm
    {
        get => NewEntryType == DictionaryEntryType.Term;
        set { if (value) NewEntryType = DictionaryEntryType.Term; }
    }

    // Edit modal
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private DictionaryEntry? _editEntry;
    [ObservableProperty] private string _editOriginal = "";
    [ObservableProperty] private string _editReplacement = "";
    [ObservableProperty] private bool _editCaseSensitive;

    // Entry count for display
    public int EntryCount => FilteredEntries.Cast<object>().Count();
    public int ActiveBoostingTermCount => _dictionary.Entries.Count(entry =>
        entry.IsEnabled && entry.EntryType == DictionaryEntryType.Term);
    public string VocabularyBoostingStatusText => ActiveBoostingTermCount == 0
        ? Loc.Instance["Dictionary.BoostingNoTerms"]
        : Loc.Instance.GetString("Dictionary.BoostingReadyFormat", ActiveBoostingTermCount);

    public ObservableCollection<DictionaryEntry> Entries { get; } = [];
    public ICollectionView FilteredEntries { get; }
    public ObservableCollection<TermPackViewModel> Packs { get; } = [];
    public ObservableCollection<LocalizedIndustryPresetOption> IndustryPresets { get; } = [];

    [ObservableProperty] private string _selectedIndustryPresetId = IndustryPreset.General.Id;

    public DictionaryViewModel(
        IDictionaryService dictionary,
        ISettingsService settings,
        LicenseService? license = null,
        TermPackRegistryService? termPackRegistry = null)
    {
        _dictionary = dictionary;
        _settings = settings;
        _license = license;
        _termPackRegistry = termPackRegistry;
        _vocabularyBoostingEnabled = _settings.Current.VocabularyBoostingEnabled;
        _selectedIndustryPresetId = IndustryPreset.Resolve(_settings.Current.SelectedIndustryPresetId).Id;

        FilteredEntries = CollectionViewSource.GetDefaultView(Entries);
        FilteredEntries.Filter = FilterByTab;

        _dictionary.EntriesChanged += RefreshEntries;
        if (_license is not null)
            _license.PropertyChanged += OnLicenseChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        RefreshEntries();
        ReconcileCommercialPackAccess();
        InitializeIndustryPresets();
        InitializePacks();
        _ = LoadRemotePacksAsync();
    }

    partial void OnSelectedTabChanged(int value)
    {
        FilteredEntries.Refresh();
        OnPropertyChanged(nameof(EntryCount));
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        FilteredEntries.Refresh();
        OnPropertyChanged(nameof(EntryCount));
    }

    partial void OnVocabularyBoostingEnabledChanged(bool value)
    {
        if (_settings.Current.VocabularyBoostingEnabled == value)
            return;

        _settings.Save(_settings.Current with { VocabularyBoostingEnabled = value });
    }

    partial void OnSelectedIndustryPresetIdChanged(string value) =>
        ApplyIndustryPreset(value);

    partial void OnNewEntryTypeChanged(DictionaryEntryType value)
    {
        OnPropertyChanged(nameof(IsNewTypeCorrection));
        OnPropertyChanged(nameof(IsNewTypeTerm));
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private bool FilterByTab(object obj)
    {
        if (obj is not DictionaryEntry entry) return false;

        // Tab filter
        var tabMatch = SelectedTab switch
        {
            1 => entry.EntryType == DictionaryEntryType.Term,
            2 => entry.EntryType == DictionaryEntryType.Correction,
            _ => true // 0=Alle, 3=Packs (entries hidden, packs shown)
        };
        if (!tabMatch) return false;

        // Search filter
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            return entry.Original.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (entry.Replacement?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        return true;
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
        if (!CanUsePack(pack.Pack))
            return;

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
        var visiblePackIds = Packs.Select(p => p.Pack.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledIds = _settings.Current.EnabledPackIds
            .Where(id => !visiblePackIds.Contains(id))
            .Concat(Packs.Where(p => p.IsEnabled).Select(p => p.Pack.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _settings.Save(_settings.Current with { EnabledPackIds = enabledIds });
    }

    private void InitializePacks()
    {
        Packs.Clear();
        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in VisiblePacks())
        {
            Packs.Add(new TermPackViewModel(pack, enabledIds.Contains(pack.Id)));
        }
    }

    private void InitializeIndustryPresets()
    {
        IndustryPresets.Clear();
        foreach (var preset in IndustryPreset.All)
            IndustryPresets.Add(CreateIndustryPresetOption(preset));
    }

    private static LocalizedIndustryPresetOption CreateIndustryPresetOption(IndustryPreset preset) =>
        new(preset.Id, Loc.Instance[$"IndustryPreset.{preset.Id}.Name"], preset.TermPackId);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(InitializeIndustryPresets);
            return;
        }

        InitializeIndustryPresets();
    }

    public void ApplyIndustryPreset(string? presetId)
    {
        var preset = IndustryPreset.Resolve(presetId);
        var current = _settings.Current;
        var enableVocabulary = current.VocabularyBoostingEnabled || preset.TermPackId is not null;

        _settings.Save(current with
        {
            SelectedIndustryPresetId = preset.Id,
            VocabularyBoostingEnabled = enableVocabulary
        });
        VocabularyBoostingEnabled = enableVocabulary;

        if (preset.TermPackId is null || !HasCommercialLicense)
            return;

        var pack = FindPackById(preset.TermPackId);
        if (pack is null)
            return;

        _dictionary.ActivatePack(pack);
        RefreshEntries();

        var enabledIds = _settings.Current.EnabledPackIds
            .Append(pack.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _settings.Save(_settings.Current with { EnabledPackIds = enabledIds });
        InitializePacks();
    }

    private void ReconcileCommercialPackAccess()
    {
        if (HasCommercialLicense)
            return;

        var currentIds = _settings.Current.EnabledPackIds;
        var commercialPackIds = GetAllPacks()
            .Where(pack => pack.RequiresCommercialLicense)
            .Select(pack => pack.Id)
            .Concat(TermPack.IndustryPackIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedIds = currentIds
            .Where(id => !commercialPackIds.Contains(id))
            .ToArray();

        foreach (var packId in currentIds.Where(id => commercialPackIds.Contains(id)))
            _dictionary.DeactivatePack(packId);

        if (allowedIds.Length != currentIds.Length)
            _settings.Save(_settings.Current with { EnabledPackIds = allowedIds });
    }

    private void OnLicenseChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(LicenseService.HasCommercialLicense))
            return;

        if (HasCommercialLicense)
        {
            ActivateEnabledRemotePacks();
            ApplyIndustryPreset(_settings.Current.SelectedIndustryPresetId);
        }
        else
        {
            ReconcileCommercialPackAccess();
        }
        InitializePacks();
    }

    private bool HasCommercialLicense => _license?.HasCommercialLicense == true;

    private bool CanUsePack(TermPack pack) =>
        HasCommercialLicense || !pack.RequiresCommercialLicense;

    private IEnumerable<TermPack> GetAllPacks() =>
        TermPack.AllPacks
            .Concat(_remotePacks)
            .GroupBy(pack => pack.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());

    private IEnumerable<TermPack> VisiblePacks() =>
        GetAllPacks().Where(CanUsePack);

    private TermPack? FindPackById(string id) =>
        GetAllPacks().FirstOrDefault(pack => pack.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task LoadRemotePacksAsync()
    {
        if (_termPackRegistry is null)
            return;

        var packs = await _termPackRegistry.GetRemotePacksAsync();
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyRemotePacks(packs);
            return;
        }

        await dispatcher.InvokeAsync(() => ApplyRemotePacks(packs));
    }

    private void ApplyRemotePacks(IReadOnlyList<TermPack> packs)
    {
        _remotePacks = packs;
        ReconcileCommercialPackAccess();
        ActivateEnabledRemotePacks();

        if (HasCommercialLicense)
            ApplyIndustryPreset(_settings.Current.SelectedIndustryPresetId);

        InitializePacks();
    }

    private void ActivateEnabledRemotePacks()
    {
        if (_remotePacks.Count == 0)
            return;

        var enabledIds = _settings.Current.EnabledPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in _remotePacks.Where(pack => enabledIds.Contains(pack.Id) && CanUsePack(pack)))
            _dictionary.ActivatePack(pack);

        RefreshEntries();
    }

    private void RefreshEntries()
    {
        Entries.Clear();
        foreach (var e in _dictionary.Entries)
            Entries.Add(e);
        FilteredEntries.Refresh();
        OnPropertyChanged(nameof(EntryCount));
        OnPropertyChanged(nameof(ActiveBoostingTermCount));
        OnPropertyChanged(nameof(VocabularyBoostingStatusText));
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
