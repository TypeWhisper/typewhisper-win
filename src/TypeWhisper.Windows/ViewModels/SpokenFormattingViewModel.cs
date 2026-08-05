using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents a language option for a spoken formatting profile.
/// </summary>
public sealed record SpokenFormattingLanguageOption(string Code, string DisplayName);

/// <summary>
/// Represents a strategy option for a spoken formatting profile.
/// </summary>
public sealed record SpokenFormattingStrategyOption(SpokenFormattingStrategy Value, string DisplayName);

/// <summary>
/// Manages the spoken formatting profile selected in Dictation settings.
/// </summary>
public sealed partial class SpokenFormattingViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ModelManagerService _modelManager;
    private readonly SpokenFormattingProfileStore _profileStore;
    private readonly SpokenFormattingStrategyResolver _resolver;
    private readonly SpokenFormattingRulesLoader _rulesLoader;
    private bool _isRefreshing;
    private string _languageHintSignature = "";
    private string? _engineId;
    private string? _modelId;

    [ObservableProperty] private string _selectedLanguageCode = "en";
    [ObservableProperty] private SpokenFormattingStrategy _selectedStrategy = SpokenFormattingStrategy.Automatic;
    [ObservableProperty] private SpokenFormattingVerificationState _verificationState;
    [ObservableProperty] private bool _hasSelectedModel;
    [ObservableProperty] private string _currentEngineDisplayName = "";
    [ObservableProperty] private string _currentModelDisplayName = "";

    /// <summary>Gets the supported profile languages.</summary>
    public ObservableCollection<SpokenFormattingLanguageOption> AvailableLanguages { get; } = [];

    /// <summary>Gets the available profile strategies.</summary>
    public ObservableCollection<SpokenFormattingStrategyOption> AvailableStrategies { get; } = [];

    /// <summary>Gets the guided verification scenarios for the selected language.</summary>
    public ObservableCollection<SpokenFormattingVerificationScenario> VerificationScenarios { get; } = [];

    /// <summary>Gets whether no global transcription model is available for a profile.</summary>
    public bool HasNoSelectedModel => !HasSelectedModel;

    /// <summary>Gets the localized profile language name.</summary>
    public string SelectedLanguageDisplayName =>
        AvailableLanguages.FirstOrDefault(option => option.Code == SelectedLanguageCode)?.DisplayName
        ?? SelectedLanguageCode;

    /// <summary>Gets the localized verification status.</summary>
    public string VerificationStatusText => VerificationState switch
    {
        SpokenFormattingVerificationState.VendorHint => Loc.Instance["SpokenFormatting.StatusVendorHint"],
        SpokenFormattingVerificationState.UserVerifiedGood => Loc.Instance["SpokenFormatting.StatusVerifiedGood"],
        SpokenFormattingVerificationState.UserVerifiedBad => Loc.Instance["SpokenFormatting.StatusVerifiedBad"],
        _ => Loc.Instance["SpokenFormatting.StatusUnknown"]
    };

    /// <summary>Initializes a spoken formatting settings view model.</summary>
    public SpokenFormattingViewModel(
        ISettingsService settings,
        ModelManagerService modelManager,
        SpokenFormattingProfileStore profileStore,
        SpokenFormattingStrategyResolver resolver,
        SpokenFormattingRulesLoader rulesLoader)
    {
        _settings = settings;
        _modelManager = modelManager;
        _profileStore = profileStore;
        _resolver = resolver;
        _rulesLoader = rulesLoader;

        BuildLocalizedOptions();
        Refresh(settings.Current, adoptConfiguredLanguage: true);
        _settings.SettingsChanged += OnSettingsChanged;
        _modelManager.PluginManager.PluginStateChanged += (_, _) => RunOnUiThread(() =>
            Refresh(_settings.Current, adoptConfiguredLanguage: false));
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    partial void OnSelectedLanguageCodeChanged(string value)
    {
        if (_isRefreshing)
            return;

        var normalized = SpokenFormattingLanguageNormalizer.Normalize(value);
        if (normalized is null || !_rulesLoader.Supports(normalized))
        {
            Refresh(_settings.Current, adoptConfiguredLanguage: true);
            return;
        }

        RefreshProfile();
    }

    partial void OnSelectedStrategyChanged(SpokenFormattingStrategy value)
    {
        if (_isRefreshing || !HasSelectedModel || _engineId is null || _modelId is null)
            return;

        var verification = value switch
        {
            SpokenFormattingStrategy.NativeOnly => SpokenFormattingVerificationState.UserVerifiedGood,
            SpokenFormattingStrategy.FallbackOnly => SpokenFormattingVerificationState.UserVerifiedBad,
            _ => VerificationState
        };
        _profileStore.SaveUserOverride(
            _engineId,
            _modelId,
            SelectedLanguageCode,
            value,
            verification,
            updateVerificationDate: value != SpokenFormattingStrategy.Automatic);
        RefreshProfile();
    }

    partial void OnVerificationStateChanged(SpokenFormattingVerificationState value) =>
        OnPropertyChanged(nameof(VerificationStatusText));

    partial void OnHasSelectedModelChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoSelectedModel));
        KeepAutomaticCommand.NotifyCanExecuteChanged();
        UseFallbackCommand.NotifyCanExecuteChanged();
        NativeWorksCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanVerifyProfile))]
    private void KeepAutomatic() => SaveVerificationChoice(
        SpokenFormattingStrategy.Automatic,
        VerificationState,
        updateVerificationDate: false);

    [RelayCommand(CanExecute = nameof(CanVerifyProfile))]
    private void UseFallback() => SaveVerificationChoice(
        SpokenFormattingStrategy.FallbackOnly,
        SpokenFormattingVerificationState.UserVerifiedBad,
        updateVerificationDate: true);

    [RelayCommand(CanExecute = nameof(CanVerifyProfile))]
    private void NativeWorks() => SaveVerificationChoice(
        SpokenFormattingStrategy.NativeOnly,
        SpokenFormattingVerificationState.UserVerifiedGood,
        updateVerificationDate: true);

    private bool CanVerifyProfile() => HasSelectedModel;

    private void SaveVerificationChoice(
        SpokenFormattingStrategy strategy,
        SpokenFormattingVerificationState verification,
        bool updateVerificationDate)
    {
        if (_engineId is null || _modelId is null)
            return;

        _profileStore.SaveUserOverride(
            _engineId,
            _modelId,
            SelectedLanguageCode,
            strategy,
            verification,
            updateVerificationDate);
        RefreshProfile();
    }

    private void OnSettingsChanged(AppSettings settings) => RunOnUiThread(() =>
    {
        var signature = BuildLanguageHintSignature(settings);
        Refresh(settings, adoptConfiguredLanguage: signature != _languageHintSignature);
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => RunOnUiThread(() =>
    {
        BuildLocalizedOptions();
        Refresh(_settings.Current, adoptConfiguredLanguage: false);
    });

    private void Refresh(AppSettings settings, bool adoptConfiguredLanguage)
    {
        _isRefreshing = true;
        try
        {
            _languageHintSignature = BuildLanguageHintSignature(settings);
            if (adoptConfiguredLanguage)
                SelectedLanguageCode = ResolveInitialLanguage(settings);

            ResolveSelectedModel(settings.SelectedModelId);
            RefreshProfileCore();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshProfile()
    {
        _isRefreshing = true;
        try
        {
            RefreshProfileCore();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RefreshProfileCore()
    {
        var context = _resolver.Resolve(
            _engineId,
            _modelId,
            [SelectedLanguageCode],
            detectedLanguage: null);
        SelectedStrategy = context?.Strategy ?? SpokenFormattingStrategy.Automatic;
        VerificationState = context?.Profile.VerificationState ?? SpokenFormattingVerificationState.Unknown;

        VerificationScenarios.Clear();
        if (_rulesLoader.RuleSetFor(SelectedLanguageCode) is { } ruleSet)
        {
            foreach (var scenario in ruleSet.VerificationScenarios)
                VerificationScenarios.Add(scenario);
        }

        OnPropertyChanged(nameof(SelectedLanguageDisplayName));
    }

    private void ResolveSelectedModel(string? fullModelId)
    {
        _engineId = null;
        _modelId = null;
        CurrentEngineDisplayName = "";
        CurrentModelDisplayName = "";

        if (string.IsNullOrWhiteSpace(fullModelId))
        {
            HasSelectedModel = false;
            return;
        }

        var identity = _modelManager.ResolveTranscriptionIdentity(fullModelId);
        if (identity is null)
        {
            HasSelectedModel = false;
            return;
        }

        var (selectionId, _) = ModelManagerService.ParsePluginModelId(fullModelId);
        var engine = _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(candidate =>
            string.Equals(candidate.GetTranscriptionSelectionId(), selectionId, StringComparison.OrdinalIgnoreCase));
        var model = engine?.TranscriptionModels.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, identity.ModelId, StringComparison.OrdinalIgnoreCase));

        _engineId = identity.EngineId;
        _modelId = identity.ModelId;
        CurrentEngineDisplayName = engine?.ProviderDisplayName ?? identity.EngineId;
        CurrentModelDisplayName = model?.DisplayName ?? identity.ModelId;
        HasSelectedModel = true;
    }

    private string ResolveInitialLanguage(AppSettings settings)
    {
        foreach (var hint in settings.GetLanguageHints())
        {
            var normalized = SpokenFormattingLanguageNormalizer.Normalize(hint);
            if (normalized is not null && _rulesLoader.Supports(normalized))
                return normalized;
        }

        var uiLanguage = SpokenFormattingLanguageNormalizer.Normalize(Loc.Instance.CurrentLanguage);
        return uiLanguage is not null && _rulesLoader.Supports(uiLanguage) ? uiLanguage : "en";
    }

    private void BuildLocalizedOptions()
    {
        var selectedLanguage = SelectedLanguageCode;
        var selectedStrategy = SelectedStrategy;
        AvailableLanguages.Clear();
        AvailableLanguages.Add(new("de", Loc.Instance["SpokenFormatting.LanguageGerman"]));
        AvailableLanguages.Add(new("en", Loc.Instance["SpokenFormatting.LanguageEnglish"]));
        AvailableStrategies.Clear();
        AvailableStrategies.Add(new(SpokenFormattingStrategy.NativeOnly, Loc.Instance["SpokenFormatting.StrategyNative"]));
        AvailableStrategies.Add(new(SpokenFormattingStrategy.Automatic, Loc.Instance["SpokenFormatting.StrategyAutomatic"]));
        AvailableStrategies.Add(new(SpokenFormattingStrategy.FallbackOnly, Loc.Instance["SpokenFormatting.StrategyFallback"]));
        SelectedLanguageCode = selectedLanguage;
        SelectedStrategy = selectedStrategy;
        OnPropertyChanged(nameof(SelectedLanguageDisplayName));
        OnPropertyChanged(nameof(VerificationStatusText));
    }

    private static string BuildLanguageHintSignature(AppSettings settings) =>
        string.Join("|", settings.GetLanguageHints());

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else
            action();
    }
}
