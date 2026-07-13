using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents localized industry preset option data.
/// </summary>
/// <param name="Id">Id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
/// <param name="TermPackId">Term pack id supplied to the member.</param>
public sealed record LocalizedIndustryPresetOption(string Id, string DisplayName, string? TermPackId);

/// <summary>
/// Provides dictionary view model behavior.
/// </summary>
public partial class DictionaryViewModel : ObservableObject, IDisposable
{
    private readonly IDictionaryService _dictionary;
    private readonly ISettingsService _settings;
    private readonly LicenseService? _license;
    private readonly TermPackRegistryService? _termPackRegistry;
    private IReadOnlyList<TermPack> _remotePacks = [];

    // Tab: 0=Alle, 1=Begriffe, 2=Korrekturen, 3=Automatisch gelernt, 4=Packs
    [ObservableProperty] private int _selectedTab;

    // Search
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _vocabularyBoostingEnabled;

    /// <summary>
    /// Returns whether search text.
    /// </summary>
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    // Add form
    [ObservableProperty] private string _newOriginal = "";
    [ObservableProperty] private string _newReplacement = "";
    [ObservableProperty] private DictionaryEntryType _newEntryType = DictionaryEntryType.Correction;
    [ObservableProperty] private bool _newCaseSensitive;

    // Segmented button helpers
    /// <summary>
    /// Gets whether is new type correction.
    /// </summary>
    public bool IsNewTypeCorrection
    {
        get => NewEntryType == DictionaryEntryType.Correction;
        set { if (value) NewEntryType = DictionaryEntryType.Correction; }
    }

    /// <summary>
    /// Gets whether is new type term.
    /// </summary>
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
    /// <summary>
    /// Performs entry count.
    /// </summary>
    public int EntryCount => _visibleEntries.Sum(item => item.Entries.Count);
    /// <summary>
    /// Performs active boosting term count.
    /// </summary>
    public int ActiveBoostingTermCount => _dictionary.Entries.Count(entry =>
        entry.IsEnabled && entry.EntryType == DictionaryEntryType.Term);
    /// <summary>
    /// Gets the number of automatically learned corrections.
    /// </summary>
    public int AutoLearnedCorrectionCount => _dictionary.Entries.Count(entry =>
        entry.EntryType == DictionaryEntryType.Correction &&
        entry.Source == DictionaryEntrySource.AutoLearned);
    /// <summary>
    /// Gets the number of entries created outside term packs.
    /// </summary>
    public int CustomDictionaryEntryCount => _dictionary.Entries.Count(entry => !IsPackEntry(entry));
    /// <summary>
    /// Gets the number of currently enabled term packs.
    /// </summary>
    public int EnabledPackCount => _settings.Current.EnabledPackIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    /// <summary>
    /// Gets the number of entries installed by term packs.
    /// </summary>
    public int InstalledPackEntryCount => _dictionary.Entries.Count(IsPackEntry);
    /// <summary>
    /// Gets the vocabulary boosting status text.
    /// </summary>
    public string VocabularyBoostingStatusText => ActiveBoostingTermCount == 0
        ? Loc.Instance["Dictionary.BoostingNoTerms"]
        : Loc.Instance.GetString("Dictionary.BoostingReadyFormat", ActiveBoostingTermCount);

    /// <summary>
    /// Gets the configured dictionary entries.
    /// </summary>
    public ObservableCollection<DictionaryEntry> Entries { get; } = [];
    /// <summary>
    /// Gets the visible dictionary presentation items.
    /// </summary>
    public System.Collections.IEnumerable VisibleEntries => _visibleEntries;
    internal ObservableCollection<DictionaryDisplayItem> VisibleDisplayItems => _visibleEntries;
    private readonly ObservableCollection<DictionaryDisplayItem> _visibleEntries = [];
    /// <summary>
    /// Gets the packs.
    /// </summary>
    public ObservableCollection<TermPackViewModel> Packs { get; } = [];
    /// <summary>
    /// Gets the industry presets.
    /// </summary>
    public ObservableCollection<LocalizedIndustryPresetOption> IndustryPresets { get; } = [];

    [ObservableProperty] private string _selectedIndustryPresetId = IndustryPreset.General.Id;

    internal Func<string, string, bool> ConfirmReset { get; set; } = static (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    /// <summary>
    /// Initializes a new instance of the DictionaryViewModel class.
    /// </summary>
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

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        _dictionary.EntriesChanged -= RefreshEntries;
        if (_license is not null)
            _license.PropertyChanged -= OnLicenseChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    partial void OnSelectedTabChanged(int value)
    {
        RefreshVisibleEntries();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        RefreshVisibleEntries();
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

    [RelayCommand(CanExecute = nameof(CanClearAutoLearnedCorrections))]
    private void ClearAutoLearnedCorrections()
    {
        var entries = _dictionary.Entries
            .Where(entry => entry.EntryType == DictionaryEntryType.Correction &&
                entry.Source == DictionaryEntrySource.AutoLearned)
            .ToArray();
        if (entries.Length == 0 || !ConfirmReset(
                Loc.Instance.GetString("Dictionary.ClearAutoLearnedConfirm", entries.Length),
                Loc.Instance["Dictionary.ResetConfirmTitle"]))
        {
            return;
        }

        _dictionary.DeleteEntries(entries.Select(entry => entry.Id));
    }

    private bool CanClearAutoLearnedCorrections() => AutoLearnedCorrectionCount > 0;

    [RelayCommand(CanExecute = nameof(CanResetCustomDictionary))]
    private void ResetCustomDictionary()
    {
        var entries = _dictionary.Entries.Where(entry => !IsPackEntry(entry)).ToArray();
        if (entries.Length == 0)
            return;

        var termCount = entries.Count(entry => entry.EntryType == DictionaryEntryType.Term);
        var autoLearnedCount = entries.Count(entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            entry.Source == DictionaryEntrySource.AutoLearned);
        var manualCorrectionCount = entries.Length - termCount - autoLearnedCount;
        if (!ConfirmReset(
                Loc.Instance.GetString(
                    "Dictionary.ResetCustomDictionaryConfirm",
                    termCount,
                    manualCorrectionCount,
                    autoLearnedCount),
                Loc.Instance["Dictionary.ResetConfirmTitle"]))
        {
            return;
        }

        _dictionary.DeleteEntries(entries.Select(entry => entry.Id));
    }

    private bool CanResetCustomDictionary() => CustomDictionaryEntryCount > 0;

    [RelayCommand(CanExecute = nameof(CanDeactivateAllTermPacks))]
    private void DeactivateAllTermPacks()
    {
        var packEntries = _dictionary.Entries.Where(IsPackEntry).ToArray();
        var enabledPackCount = EnabledPackCount;
        if ((packEntries.Length == 0 && enabledPackCount == 0) || !ConfirmReset(
                Loc.Instance.GetString(
                    "Dictionary.DeactivateAllPacksConfirm",
                    enabledPackCount,
                    packEntries.Length),
                Loc.Instance["Dictionary.ResetConfirmTitle"]))
        {
            return;
        }

        if (packEntries.Length > 0)
            _dictionary.DeleteEntries(packEntries.Select(entry => entry.Id));

        _settings.Save(_settings.Current with { EnabledPackIds = [] });
        foreach (var pack in Packs)
            pack.IsEnabled = false;
        NotifyResetStateChanged();
    }

    private bool CanDeactivateAllTermPacks() => EnabledPackCount > 0 || InstalledPackEntryCount > 0;

    private bool MatchesSelectedTab(DictionaryEntry entry)
    {
        return SelectedTab switch
        {
            1 => entry.EntryType == DictionaryEntryType.Term,
            2 => entry.EntryType == DictionaryEntryType.Correction,
            3 => entry.EntryType == DictionaryEntryType.Correction &&
                entry.Source == DictionaryEntrySource.AutoLearned,
            _ => true // 0=Alle, 4=Packs (entries hidden, packs shown)
        };
    }

    private void RefreshVisibleEntries()
    {
        var candidates = Entries.Where(MatchesSelectedTab).ToArray();
        var correctionGroups = candidates
            .Where(entry => entry.EntryType == DictionaryEntryType.Correction &&
                entry.Replacement is { Length: > 0 })
            .GroupBy(entry => entry.Replacement!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var emittedReplacements = new HashSet<string>(StringComparer.Ordinal);
        var search = SearchText.Trim();

        _visibleEntries.Clear();
        foreach (var entry in candidates)
        {
            IReadOnlyList<DictionaryEntry> entries = [entry];
            if (entry.EntryType == DictionaryEntryType.Correction &&
                entry.Replacement is { Length: > 0 } replacement)
            {
                if (!emittedReplacements.Add(replacement))
                    continue;

                entries = correctionGroups[replacement];
            }

            var replacementMatch = search.Length > 0 &&
                entries[0].Replacement?.Contains(search, StringComparison.OrdinalIgnoreCase) is true;
            var aliasMatch = search.Length > 0 &&
                entries.Any(candidate => candidate.Original.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (search.Length > 0 && !replacementMatch && !aliasMatch)
                continue;

            _visibleEntries.Add(new DictionaryDisplayItem(entries, aliasMatch && entries.Count > 1));
        }

        OnPropertyChanged(nameof(EntryCount));
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
            Replacement = NewEntryType == DictionaryEntryType.Correction ? NewReplacement.Trim() : null,
            CaseSensitive = NewCaseSensitive
        });

        NewOriginal = "";
        NewReplacement = "";
        NewCaseSensitive = false;
    }

    [RelayCommand]
    private void PrepareAlias(string? replacement)
    {
        if (replacement is not { Length: > 0 })
            return;

        NewEntryType = DictionaryEntryType.Correction;
        NewOriginal = "";
        NewReplacement = replacement;
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
            Replacement = EditEntry.EntryType == DictionaryEntryType.Correction ? EditReplacement.Trim() : null,
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
        NotifyResetStateChanged();
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

    /// <summary>
    /// Applies industry preset.
    /// </summary>
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
        NotifyResetStateChanged();
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
        {
            _settings.Save(_settings.Current with { EnabledPackIds = allowedIds });
            NotifyResetStateChanged();
        }
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
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(RefreshEntries);
            return;
        }

        Entries.Clear();
        foreach (var e in _dictionary.Entries)
            Entries.Add(e);
        RefreshVisibleEntries();
        OnPropertyChanged(nameof(ActiveBoostingTermCount));
        OnPropertyChanged(nameof(VocabularyBoostingStatusText));
        NotifyResetStateChanged();
    }

    private void NotifyResetStateChanged()
    {
        OnPropertyChanged(nameof(AutoLearnedCorrectionCount));
        OnPropertyChanged(nameof(CustomDictionaryEntryCount));
        OnPropertyChanged(nameof(EnabledPackCount));
        OnPropertyChanged(nameof(InstalledPackEntryCount));
        ClearAutoLearnedCorrectionsCommand.NotifyCanExecuteChanged();
        ResetCustomDictionaryCommand.NotifyCanExecuteChanged();
        DeactivateAllTermPacksCommand.NotifyCanExecuteChanged();
    }

    private static bool IsPackEntry(DictionaryEntry entry) =>
        entry.Id.StartsWith("pack:", StringComparison.Ordinal);
}

internal sealed partial class DictionaryDisplayItem : ObservableObject
{
    public IReadOnlyList<DictionaryEntry> Entries { get; }
    public DictionaryEntry PrimaryEntry => Entries[0];
    public string? Replacement => PrimaryEntry.Replacement;
    public bool IsGroupedCorrection => Entries.Count > 1;
    [ObservableProperty] private bool _isExpanded;

    public DictionaryDisplayItem(IReadOnlyList<DictionaryEntry> entries, bool isExpanded = false)
    {
        Entries = entries;
        _isExpanded = isExpanded;
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// Provides term pack view model behavior.
/// </summary>
public partial class TermPackViewModel : ObservableObject
{
    /// <summary>
    /// Gets the pack.
    /// </summary>
    public TermPack Pack { get; }
    [ObservableProperty] private bool _isEnabled;

    /// <summary>
    /// Performs terms preview.
    /// </summary>
    public string TermsPreview => string.Join(", ", Pack.Terms.Take(8)) +
        (Pack.Terms.Length > 8 ? $" +{Pack.Terms.Length - 8}" : "");

    /// <summary>
    /// Initializes a new instance of the TermPackViewModel class.
    /// </summary>
    public TermPackViewModel(TermPack pack, bool isEnabled)
    {
        Pack = pack;
        _isEnabled = isEnabled;
    }
}
