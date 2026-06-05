using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents welcome model item data.
/// </summary>
/// <param name="FullModelId">Full model id supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
/// <param name="SizeDescription">Size description supplied to the member.</param>
/// <param name="IsRecommended">Is recommended supplied to the member.</param>
public record WelcomeModelItem(string FullModelId, string DisplayName, string? SizeDescription, bool IsRecommended);

/// <summary>
/// Represents welcome completion request data.
/// </summary>
/// <param name="SettingsRoute">Settings route supplied to the member.</param>
/// <param name="PluginIdToConfigure">Plugin identifier to open for configuration after welcome completes.</param>
public sealed record WelcomeCompletionRequest(SettingsRoute? SettingsRoute, string? PluginIdToConfigure)
{
    /// <summary>
    /// Creates a new value using the supplied arguments.
    /// </summary>
    public static WelcomeCompletionRequest None { get; } = new(null, null);
}

/// <summary>
/// Provides welcome view model behavior.
/// </summary>
public partial class WelcomeViewModel : ObservableObject
{
    private const string LocalPluginId = "com.typewhisper.sherpa-onnx";
    private const string GroqPluginId = "com.typewhisper.groq";
    private const string ParakeetModelId = "plugin:com.typewhisper.sherpa-onnx:parakeet-tdt-0.6b";
    private const string PreferredGroqModelId = "plugin:com.typewhisper.groq:whisper-large-v3-turbo";

    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;
    private readonly PluginRegistryService _registry;
    private readonly DictationViewModel _dictation;
    private readonly DictionaryViewModel _dictionary;
    private readonly EventHandler _pluginStateChangedHandler;
    private readonly Dispatcher? _uiDispatcher;
    private readonly Dictionary<string, (ITranscriptionEnginePlugin Plugin, UserControl? View)> _settingsViewCache = [];
    private bool _isInitializing;
    private bool _isMicTestRunning;
    private DictationState _lastObservedDictationState;

    [ObservableProperty] private int _currentStep; // 0=Extensions+Model, 1=Mic, 2=Hotkey, 3=Try it out
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private string? _selectedModelId;
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private bool _micWorking;
    [ObservableProperty] private string _mainDictationHotkey = "";
    [ObservableProperty] private string _newMainDictationHotkey = "";
    [ObservableProperty] private bool _isLoadingPlugins;
    [ObservableProperty] private string _trialText = "";
    [ObservableProperty] private bool _trialSuccess;
    [ObservableProperty] private string _trialInlineStatus = "";
    [ObservableProperty] private string _selectedIndustryPresetId = IndustryPreset.General.Id;

    /// <summary>
    /// Gets the loaded plugin view models.
    /// </summary>
    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; } = [];
    /// <summary>
    /// Gets the available models.
    /// </summary>
    public ObservableCollection<WelcomeModelItem> AvailableModels { get; } = [];
    /// <summary>
    /// Gets the industry presets.
    /// </summary>
    public ObservableCollection<LocalizedIndustryPresetOption> IndustryPresets => _dictionary.IndustryPresets;
    /// <summary>
    /// Gets the microphones.
    /// </summary>
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];

    /// <summary>
    /// Gets the configured main dictation hotkeys shown during onboarding.
    /// </summary>
    public ObservableCollection<string> MainDictationHotkeys { get; } = [];

    /// <summary>
    /// Gets the settings route requested when onboarding completes.
    /// </summary>
    public WelcomeCompletionRequest CompletionRequest { get; private set; } = WelcomeCompletionRequest.None;
    /// <summary>
    /// Raised when playback or the asynchronous operation completes.
    /// </summary>
    public event EventHandler? Completed;
    private bool _isSyncingMainDictationHotkeys;

    /// <summary>
    /// Initializes a new instance of the WelcomeViewModel class.
    /// </summary>
    public WelcomeViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioRecordingService audio,
        PluginRegistryService registry,
        DictationViewModel dictation,
        DictionaryViewModel dictionary)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audio = audio;
        _registry = registry;
        _dictation = dictation;
        _dictionary = dictionary;
        _uiDispatcher = CaptureActiveDispatcher();
        _lastObservedDictationState = dictation.State;

        _isInitializing = true;
        ReplaceCollection(MainDictationHotkeys, ResolveMainDictationHotkeys(settings.Current));
        MainDictationHotkey = FirstOrEmpty(MainDictationHotkeys);
        SelectedIndustryPresetId = IndustryPreset.Resolve(settings.Current.SelectedIndustryPresetId).Id;
        _isInitializing = false;

        _pluginStateChangedHandler = (_, _) => DispatchToUi(RefreshModels);
        _modelManager.PluginManager.PluginStateChanged += _pluginStateChangedHandler;
        _modelManager.PropertyChanged += OnModelManagerChanged;
        _dictation.PropertyChanged += OnDictationPropertyChanged;

        RefreshMicrophones();
        _ = LoadPluginsAsync();
    }

    /// <summary>
    /// Gets whether is first step.
    /// </summary>
    public bool IsFirstStep => CurrentStep == 0;
    /// <summary>
    /// Gets whether is final step.
    /// </summary>
    public bool IsFinalStep => CurrentStep == 3;
    /// <summary>
    /// Gets the primary action label.
    /// </summary>
    public string PrimaryActionLabel => IsFinalStep ? Loc.Instance["Welcome.GetStarted"] : Loc.Instance["Welcome.Next"];

    /// <summary>
    /// Gets the display text for all configured main dictation hotkeys.
    /// </summary>
    public string MainDictationHotkeyDisplay => MainDictationHotkeys.Count == 0
        ? Loc.Instance["Hotkey.ClickToAssign"]
        : string.Join(", ", MainDictationHotkeys);

    /// <summary>
    /// Gets whether onboarding has at least one main dictation hotkey configured.
    /// </summary>
    public bool HasConfiguredMainHotkey => MainDictationHotkeys.Count > 0;

    /// <summary>
    /// Gets whether the selected local or plugin engine is ready.
    /// </summary>
    public bool HasReadyEngine =>
        !string.IsNullOrWhiteSpace(SelectedModelId)
        && _modelManager.GetStatus(SelectedModelId).Type == ModelStatusType.Ready;
    /// <summary>
    /// Returns selected configuration plugin.
    /// </summary>
    public bool SelectedModelNeedsConfiguration => GetSelectedConfigurationPlugin() is not null;
    /// <summary>
    /// Gets the selected model settings view.
    /// </summary>
    public UserControl? SelectedModelSettingsView =>
        GetSelectedConfigurationPlugin() is { } plugin ? GetSettingsView(plugin) : null;
    /// <summary>
    /// Gets the selected model configuration provider name.
    /// </summary>
    public string SelectedModelConfigurationProviderName =>
        GetSelectedConfigurationPlugin()?.ProviderDisplayName ?? "";
    /// <summary>
    /// Gets the selected model configuration title.
    /// </summary>
    public string SelectedModelConfigurationTitle => Loc.Instance["Welcome.SelectedModelConfigurationTitle"];
    /// <summary>
    /// Gets the selected model configuration hint.
    /// </summary>
    public string SelectedModelConfigurationHint =>
        string.IsNullOrWhiteSpace(SelectedModelConfigurationProviderName)
            ? Loc.Instance["Welcome.SelectedModelConfigurationHint"]
            : Loc.Instance.GetString(
                "Welcome.SelectedModelConfigurationHintFormat",
                SelectedModelConfigurationProviderName);
    /// <summary>
    /// Performs recommended local plugin.
    /// </summary>
    public RegistryPluginItemViewModel? RecommendedLocalPlugin => Plugins.FirstOrDefault(p => p.Id == LocalPluginId);
    /// <summary>
    /// Performs recommended cloud plugin.
    /// </summary>
    public RegistryPluginItemViewModel? RecommendedCloudPlugin => Plugins.FirstOrDefault(p => p.Id == GroqPluginId);
    /// <summary>
    /// Gets whether is local recommendation installed.
    /// </summary>
    public bool IsLocalRecommendationInstalled => LocalEngine is not null;
    /// <summary>
    /// Gets whether is cloud recommendation installed.
    /// </summary>
    public bool IsCloudRecommendationInstalled => CloudEngine is not null;
    /// <summary>
    /// Gets whether is cloud recommendation configured.
    /// </summary>
    public bool IsCloudRecommendationConfigured => CloudEngine?.IsConfigured ?? false;
    /// <summary>
    /// Gets whether is local recommendation selected.
    /// </summary>
    public bool IsLocalRecommendationSelected => SelectedModelId == ParakeetModelId;
    /// <summary>
    /// Returns whether cloud recommendation selected.
    /// </summary>
    public bool IsCloudRecommendationSelected => SelectedModelId?.StartsWith($"plugin:{GroqPluginId}:") == true;
    /// <summary>
    /// Gets the cloud recommendation action label.
    /// </summary>
    public string CloudRecommendationActionLabel =>
        IsCloudRecommendationConfigured ? Loc.Instance["Welcome.UseGroq"] : Loc.Instance["Welcome.ConfigureKey"];
    /// <summary>
    /// Returns selected model action label.
    /// </summary>
    public string SelectedModelActionLabel => GetSelectedModelActionLabel();
    /// <summary>
    /// Gets whether can apply selected model.
    /// </summary>
    public bool CanApplySelectedModel =>
        !string.IsNullOrWhiteSpace(SelectedModelId)
        && !IsDownloading
        && !SelectedModelNeedsConfiguration
        && !(SelectedModelId == _modelManager.ActiveModelId
             && _modelManager.GetStatus(SelectedModelId).Type == ModelStatusType.Ready);
    /// <summary>
    /// Returns local recommendation status.
    /// </summary>
    public string LocalRecommendationStatus => GetLocalRecommendationStatus();
    /// <summary>
    /// Returns cloud recommendation status.
    /// </summary>
    public string CloudRecommendationStatus => GetCloudRecommendationStatus();
    /// <summary>
    /// Gets whether can try it out.
    /// </summary>
    public bool CanTryItOut => HasReadyEngine && HasConfiguredMainHotkey;
    /// <summary>
    /// Gets the trial is recording.
    /// </summary>
    public bool TrialIsRecording => _dictation.State == DictationState.Recording;
    /// <summary>
    /// Gets the trial is processing.
    /// </summary>
    public bool TrialIsProcessing =>
        _dictation.State == DictationState.Processing || _dictation.State == DictationState.Inserting;
    /// <summary>
    /// Gets the trial has error.
    /// </summary>
    public bool TrialHasError => _dictation.State == DictationState.Error;
    /// <summary>
    /// Gets the trial status text.
    /// </summary>
    public string TrialStatusText => _dictation.StatusText;
    /// <summary>
    /// Gets whether show trial inline status.
    /// </summary>
    public bool ShowTrialInlineStatus =>
        !TrialSuccess
        && !TrialIsRecording
        && !TrialIsProcessing
        && !string.IsNullOrWhiteSpace(TrialInlineStatus);
    /// <summary>
    /// Gets whether uses clipboard fallback.
    /// </summary>
    public bool UsesClipboardFallback => !_settings.Current.AutoPaste;

    private ITranscriptionEnginePlugin? LocalEngine =>
        _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == LocalPluginId);

    private ITranscriptionEnginePlugin? CloudEngine =>
        _modelManager.PluginManager.TranscriptionEngines.FirstOrDefault(e => e.PluginId == GroqPluginId);

    partial void OnCurrentStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsFinalStep));
        OnPropertyChanged(nameof(PrimaryActionLabel));

        if (value == 3)
            PrepareTrialStep();
    }

    partial void OnMainDictationHotkeyChanged(string value)
    {
        OnPropertyChanged(nameof(MainDictationHotkeyDisplay));
        OnPropertyChanged(nameof(HasConfiguredMainHotkey));
        OnPropertyChanged(nameof(CanTryItOut));

        if (_isInitializing || _isSyncingMainDictationHotkeys)
            return;

        var normalized = HotkeyParser.Normalize(value);
        ReplaceCollection(
            MainDictationHotkeys,
            string.IsNullOrWhiteSpace(normalized) ? [] : [normalized]);
        PersistMainDictationHotkeys(MainDictationHotkeys);
    }

    partial void OnSelectedModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasReadyEngine));
        OnPropertyChanged(nameof(CanTryItOut));
        OnPropertyChanged(nameof(IsLocalRecommendationSelected));
        OnPropertyChanged(nameof(IsCloudRecommendationSelected));
        NotifySelectedModelConfigurationProperties();
    }

    private async Task LoadPluginsAsync()
    {
        IsLoadingPlugins = true;

        try
        {
            var registryPlugins = await _registry.FetchRegistryAsync();

            DispatchToUi(() =>
            {
                Plugins.Clear();
                foreach (var rp in registryPlugins)
                    Plugins.Add(new RegistryPluginItemViewModel(rp, _registry));
            });
        }
        finally
        {
            IsLoadingPlugins = false;
        }

        RefreshModels();
    }

    private void RefreshModels()
    {
        AvailableModels.Clear();

        var collectedModels = new List<WelcomeModelItem>();
        foreach (var engine in _modelManager.PluginManager.TranscriptionEngines)
        {
            var selectionId = engine.GetTranscriptionSelectionId();
            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(selectionId, model.Id);
                var name = string.IsNullOrWhiteSpace(model.SizeDescription)
                    ? model.DisplayName
                    : $"{model.DisplayName} ({model.SizeDescription})";
                if (model.IsRecommended)
                    name += $" — {Loc.Instance["Welcome.Recommended"]}";
                collectedModels.Add(new WelcomeModelItem(fullId, name, model.SizeDescription, model.IsRecommended));
            }
        }

        foreach (var model in collectedModels
                     .OrderBy(m => GetModelPriority(m.FullModelId))
                     .ThenByDescending(m => m.IsRecommended)
                     .ThenBy(m => m.DisplayName))
        {
            AvailableModels.Add(model);
        }

        // Auto-select recommended or first
        if (SelectedModelId is null || !AvailableModels.Any(m => m.FullModelId == SelectedModelId))
        {
            SelectedModelId = AvailableModels.FirstOrDefault(m => m.FullModelId == ParakeetModelId)?.FullModelId
                              ?? AvailableModels.FirstOrDefault(m => m.FullModelId == PreferredGroqModelId)?.FullModelId
                              ?? AvailableModels.FirstOrDefault(m => m.IsRecommended)?.FullModelId
                              ?? AvailableModels.FirstOrDefault()?.FullModelId;
        }

        NotifyRecommendationProperties();
    }

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (string.IsNullOrEmpty(SelectedModelId)) return;

        IsDownloading = true;
        DownloadStatus = Loc.Instance["Welcome.DownloadProgress"];

        _modelManager.PropertyChanged += OnModelManagerPropertyChanged;

        try
        {
            await _modelManager.DownloadAndLoadModelAsync(SelectedModelId);
            DownloadStatus = Loc.Instance["Welcome.Done"];
            _settings.Save(_settings.Current with { SelectedModelId = SelectedModelId });
        }
        catch (Exception ex)
        {
            DownloadStatus = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
            return;
        }
        finally
        {
            IsDownloading = false;
            _modelManager.PropertyChanged -= OnModelManagerPropertyChanged;
        }
    }

    private void OnModelManagerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ModelManagerService.GetStatus) || SelectedModelId is null)
            return;

        var status = _modelManager.GetStatus(SelectedModelId);
        if (status.Type == ModelStatusType.Downloading)
        {
            DownloadProgress = status.Progress;
            DownloadStatus = $"Download: {status.Progress:P0}";
        }
        else if (status.Type == ModelStatusType.Loading)
        {
            DownloadStatus = Loc.Instance["Welcome.LoadingModel"];
        }
    }

    [RelayCommand]
    private void SelectRecommendedLocal()
    {
        SelectedModelId = ParakeetModelId;
    }

    [RelayCommand]
    private async Task InstallRecommendedLocal()
    {
        var plugin = RecommendedLocalPlugin;
        if (plugin is null)
            return;

        await plugin.InstallCommand.ExecuteAsync(null);
        plugin.RefreshInstallState();
        NotifyRecommendationProperties();
        RefreshModels();

        if (plugin.InstallState == PluginInstallState.Installed)
            SelectRecommendedLocal();
    }

    [RelayCommand]
    private void SelectRecommendedCloud()
    {
        SelectedModelId = AvailableModels.FirstOrDefault(m => m.FullModelId == PreferredGroqModelId)?.FullModelId
                          ?? AvailableModels.FirstOrDefault(m => m.FullModelId.StartsWith($"plugin:{GroqPluginId}:"))?.FullModelId
                          ?? SelectedModelId;
    }

    [RelayCommand]
    private void AddMainDictationHotkey(string? value = null)
    {
        var normalized = HotkeyParser.Normalize(value ?? NewMainDictationHotkey);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (MainDictationHotkeys.Any(hotkey =>
                string.Equals(HotkeyParser.Normalize(hotkey), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            NewMainDictationHotkey = "";
            return;
        }

        MainDictationHotkeys.Add(normalized);
        NewMainDictationHotkey = "";
        SyncMainDictationHotkeyFromCollection();
        PersistMainDictationHotkeys(MainDictationHotkeys);
        NotifyMainHotkeyProperties();
    }

    [RelayCommand]
    private void RemoveMainDictationHotkey(string? hotkey)
    {
        var normalized = HotkeyParser.Normalize(hotkey);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var existing = MainDictationHotkeys.FirstOrDefault(value =>
            string.Equals(HotkeyParser.Normalize(value), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        MainDictationHotkeys.Remove(existing);
        SyncMainDictationHotkeyFromCollection();
        PersistMainDictationHotkeys(MainDictationHotkeys);
        NotifyMainHotkeyProperties();
    }

    [RelayCommand]
    private async Task InstallRecommendedCloud()
    {
        var plugin = RecommendedCloudPlugin;
        if (plugin is null)
            return;

        await plugin.InstallCommand.ExecuteAsync(null);
        plugin.RefreshInstallState();
        NotifyRecommendationProperties();
        RefreshModels();

        if (plugin.InstallState == PluginInstallState.Installed)
            SelectRecommendedCloud();
    }

    private void OnModelManagerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ModelManagerService.GetStatus) or nameof(ModelManagerService.ActiveModelId))
        {
            OnPropertyChanged(nameof(HasReadyEngine));
            OnPropertyChanged(nameof(CanTryItOut));
            NotifyRecommendationProperties();
        }
    }

    private void OnDictationPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DictationViewModel.State))
        {
            var newState = _dictation.State;

            if (CurrentStep == 3
                && _lastObservedDictationState == DictationState.Inserting
                && newState == DictationState.Idle)
            {
                TrialSuccess = true;
                TrialInlineStatus = string.Empty;
            }

            if (CurrentStep == 3 && newState == DictationState.Recording)
                TrialInlineStatus = string.Empty;

            _lastObservedDictationState = newState;
            OnPropertyChanged(nameof(TrialIsRecording));
            OnPropertyChanged(nameof(TrialIsProcessing));
            OnPropertyChanged(nameof(TrialHasError));
            OnPropertyChanged(nameof(TrialStatusText));
            OnPropertyChanged(nameof(ShowTrialInlineStatus));
            return;
        }

        if (args.PropertyName == nameof(DictationViewModel.StatusText))
        {
            if (CurrentStep == 3
                && _dictation.State == DictationState.Idle
                && _dictation.StatusText != Loc.Instance["Status.Ready"])
            {
                TrialInlineStatus = _dictation.StatusText;
            }

            OnPropertyChanged(nameof(TrialStatusText));
            OnPropertyChanged(nameof(ShowTrialInlineStatus));
        }
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep == 3)
        {
            Finish(openSettingsWhenEngineNotReady: true);
            return;
        }

        if (CurrentStep == 1)
            StopMicTest();

        CurrentStep++;

        if (CurrentStep == 1)
            StartMicTest();
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep == 1)
            StopMicTest();

        if (CurrentStep > 0)
        {
            CurrentStep--;

            if (CurrentStep == 1)
                StartMicTest();
        }
    }

    [RelayCommand]
    private void Skip()
    {
        Finish();
    }

    private void StartMicTest()
    {
        if (_isMicTestRunning)
            return;

        MicLevel = 0;
        MicWorking = false;
        _audio.AudioLevelChanged -= OnMicLevel;
        _audio.AudioLevelChanged += OnMicLevel;
        if (!_audio.HasDevice)
        {
            _audio.AudioLevelChanged -= OnMicLevel;
            return;
        }
        _audio.WarmUp();
        _audio.StartRecording();
        _isMicTestRunning = true;
    }

    private void StopMicTest()
    {
        if (!_isMicTestRunning)
            return;

        _audio.AudioLevelChanged -= OnMicLevel;
        _audio.StopRecording();
        _isMicTestRunning = false;
    }

    private void OnMicLevel(object? sender, AudioLevelEventArgs e)
    {
        MicLevel = e.RmsLevel;
        if (e.RmsLevel > 0.01f)
            MicWorking = true;
    }

    private void RefreshMicrophones()
    {
        Microphones.Clear();
        Microphones.Add(new MicrophoneItem(null, Loc.Instance["Microphone.Default"]));
        foreach (var (number, name) in _audio.GetAvailableInputDevices())
            Microphones.Add(new MicrophoneItem(number, name));
    }

    private void Finish(bool openSettingsWhenEngineNotReady = false)
    {
        CompletionRequest = openSettingsWhenEngineNotReady
            ? BuildCompletionRequest()
            : WelcomeCompletionRequest.None;
        OnPropertyChanged(nameof(CompletionRequest));

        StopMicTest();
        PersistMainDictationHotkeys(MainDictationHotkeys);
        _dictionary.ApplyIndustryPreset(SelectedIndustryPresetId);

        Completed?.Invoke(this, EventArgs.Empty);
    }

    private WelcomeCompletionRequest BuildCompletionRequest()
    {
        if (HasReadyEngine)
            return WelcomeCompletionRequest.None;

        return GetSelectedConfigurationPlugin() is { } plugin
            ? new WelcomeCompletionRequest(SettingsRoute.Integrations, plugin.PluginId)
            : new WelcomeCompletionRequest(SettingsRoute.Dictation, null);
    }

    /// <summary>
    /// Performs cleanup.
    /// </summary>
    public void Cleanup()
    {
        StopMicTest();
        _modelManager.PluginManager.PluginStateChanged -= _pluginStateChangedHandler;
        _modelManager.PropertyChanged -= OnModelManagerChanged;
        _dictation.PropertyChanged -= OnDictationPropertyChanged;
    }

    private static IReadOnlyList<string> ResolveMainDictationHotkeys(AppSettings settings) =>
        NormalizeHotkeyList(settings.GetMainDictationHotkeys());

    private void PersistMainDictationHotkeys(IEnumerable<string?> hotkeys)
    {
        var normalizedHotkeys = NormalizeHotkeyList(hotkeys);
        var primaryHotkey = FirstOrEmpty(normalizedHotkeys);
        var current = _settings.Current;

        if (current.GetMainDictationHotkeys().SequenceEqual(normalizedHotkeys, StringComparer.OrdinalIgnoreCase)
            && current.PushToTalkHotkey == primaryHotkey
            && current.ToggleHotkey == primaryHotkey)
        {
            return;
        }

        _settings.Save(current with
        {
            MainDictationHotkeys = normalizedHotkeys,
            PushToTalkHotkey = primaryHotkey,
            ToggleHotkey = primaryHotkey
        });
    }

    private void SyncMainDictationHotkeyFromCollection()
    {
        _isSyncingMainDictationHotkeys = true;
        try
        {
            MainDictationHotkey = FirstOrEmpty(MainDictationHotkeys);
        }
        finally
        {
            _isSyncingMainDictationHotkeys = false;
        }
    }

    private void NotifyMainHotkeyProperties()
    {
        OnPropertyChanged(nameof(MainDictationHotkeyDisplay));
        OnPropertyChanged(nameof(HasConfiguredMainHotkey));
        OnPropertyChanged(nameof(CanTryItOut));
    }

    private static IReadOnlyList<string> NormalizeHotkeyList(IEnumerable<string?> values) =>
        values.Select(HotkeyParser.Normalize)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstOrEmpty(IEnumerable<string> values) =>
        values.FirstOrDefault() ?? "";

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void NotifyRecommendationProperties()
    {
        OnPropertyChanged(nameof(RecommendedLocalPlugin));
        OnPropertyChanged(nameof(RecommendedCloudPlugin));
        OnPropertyChanged(nameof(IsLocalRecommendationInstalled));
        OnPropertyChanged(nameof(IsCloudRecommendationInstalled));
        OnPropertyChanged(nameof(IsCloudRecommendationConfigured));
        OnPropertyChanged(nameof(IsLocalRecommendationSelected));
        OnPropertyChanged(nameof(IsCloudRecommendationSelected));
        OnPropertyChanged(nameof(CloudRecommendationActionLabel));
        OnPropertyChanged(nameof(LocalRecommendationStatus));
        OnPropertyChanged(nameof(CloudRecommendationStatus));
        NotifySelectedModelConfigurationProperties();
    }

    private void NotifySelectedModelConfigurationProperties()
    {
        OnPropertyChanged(nameof(SelectedModelNeedsConfiguration));
        OnPropertyChanged(nameof(SelectedModelSettingsView));
        OnPropertyChanged(nameof(SelectedModelConfigurationProviderName));
        OnPropertyChanged(nameof(SelectedModelConfigurationTitle));
        OnPropertyChanged(nameof(SelectedModelConfigurationHint));
        OnPropertyChanged(nameof(SelectedModelActionLabel));
        OnPropertyChanged(nameof(CanApplySelectedModel));
    }

    private static int GetModelPriority(string fullModelId)
    {
        if (fullModelId == ParakeetModelId)
            return 0;

        if (fullModelId == PreferredGroqModelId)
            return 1;

        if (fullModelId.StartsWith($"plugin:{GroqPluginId}:"))
            return 2;

        return 3;
    }

    private string GetSelectedModelActionLabel()
    {
        if (string.IsNullOrWhiteSpace(SelectedModelId))
            return Loc.Instance["Welcome.Download"];

        if (SelectedModelNeedsConfiguration)
            return Loc.Instance["Welcome.ConfigureKey"];

        var status = _modelManager.GetStatus(SelectedModelId);
        if (status.Type == ModelStatusType.Ready)
        {
            return SelectedModelId == _modelManager.ActiveModelId
                ? Loc.Instance["Welcome.Ready"]
                : Loc.Instance["Welcome.UseSelectedModel"];
        }

        return Loc.Instance["Welcome.DownloadAndActivate"];
    }

    private ITranscriptionEnginePlugin? GetSelectedTranscriptionPlugin()
    {
        if (string.IsNullOrWhiteSpace(SelectedModelId)
            || !ModelManagerService.IsPluginModel(SelectedModelId))
        {
            return null;
        }

        var (pluginId, _) = ModelManagerService.ParsePluginModelId(SelectedModelId);
        return _modelManager.PluginManager.TranscriptionEngines
            .FirstOrDefault(engine => string.Equals(
                engine.GetTranscriptionSelectionId(),
                pluginId,
                StringComparison.OrdinalIgnoreCase));
    }

    private ITranscriptionEnginePlugin? GetSelectedConfigurationPlugin()
    {
        var plugin = GetSelectedTranscriptionPlugin();
        if (plugin is null || plugin.SupportsModelDownload || plugin.IsConfigured)
            return null;

        return plugin;
    }

    private UserControl? GetSettingsView(ITranscriptionEnginePlugin plugin)
    {
        var settingsViewKey = plugin.GetTranscriptionSelectionId();
        if (_settingsViewCache.TryGetValue(settingsViewKey, out var cached)
            && ReferenceEquals(cached.Plugin, plugin))
        {
            return cached.View;
        }

        var view = plugin.CreateSettingsView();
        _settingsViewCache[settingsViewKey] = (plugin, view);
        return view;
    }

    private static Dispatcher? CaptureActiveDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished
            ? null
            : dispatcher;
    }

    private void DispatchToUi(Action action)
    {
        var dispatcher = _uiDispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
    }

    private string GetLocalRecommendationStatus()
    {
        if (!IsLocalRecommendationInstalled)
            return Loc.Instance["Welcome.NotInstalled"];

        var status = _modelManager.GetStatus(ParakeetModelId);
        return status.Type switch
        {
            ModelStatusType.Ready => Loc.Instance["Welcome.WorksOfflineStatus"],
            ModelStatusType.Loading => Loc.Instance["Welcome.LoadingModel"],
            ModelStatusType.Downloading => Loc.Instance["Welcome.DownloadRequired"],
            ModelStatusType.Error => status.ErrorMessage ?? Loc.Instance.GetString("Status.ErrorFormat", ""),
            _ => Loc.Instance["Welcome.DownloadRequired"]
        };
    }

    private string GetCloudRecommendationStatus()
    {
        if (!IsCloudRecommendationInstalled)
            return Loc.Instance["Welcome.NotInstalled"];

        if (!IsCloudRecommendationConfigured)
            return Loc.Instance["Welcome.ApiKeyRequired"];

        var status = _modelManager.GetStatus(PreferredGroqModelId);
        return status.Type switch
        {
            ModelStatusType.Ready => Loc.Instance["Welcome.FastestStatus"],
            ModelStatusType.Loading => Loc.Instance["Welcome.LoadingModel"],
            ModelStatusType.Downloading => Loc.Instance["Welcome.DownloadRequired"],
            ModelStatusType.Error => status.ErrorMessage ?? Loc.Instance.GetString("Status.ErrorFormat", ""),
            _ => Loc.Instance["Welcome.UseSelectedModel"]
        };
    }

    private void PrepareTrialStep()
    {
        TrialSuccess = false;
        TrialText = string.Empty;
        TrialInlineStatus = string.Empty;
        _lastObservedDictationState = _dictation.State;
        OnPropertyChanged(nameof(TrialIsRecording));
        OnPropertyChanged(nameof(TrialIsProcessing));
        OnPropertyChanged(nameof(TrialHasError));
        OnPropertyChanged(nameof(TrialStatusText));
        OnPropertyChanged(nameof(ShowTrialInlineStatus));
    }
}
