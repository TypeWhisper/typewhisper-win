using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
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

public record WelcomeModelItem(string FullModelId, string DisplayName, string? SizeDescription, bool IsRecommended);

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
    [ObservableProperty] private bool _isLoadingPlugins;
    [ObservableProperty] private string _trialText = "";
    [ObservableProperty] private bool _trialSuccess;
    [ObservableProperty] private string _trialInlineStatus = "";

    public ObservableCollection<RegistryPluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<WelcomeModelItem> AvailableModels { get; } = [];
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];
    public event EventHandler? Completed;

    public WelcomeViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioRecordingService audio,
        PluginRegistryService registry,
        DictationViewModel dictation)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audio = audio;
        _registry = registry;
        _dictation = dictation;
        _lastObservedDictationState = dictation.State;

        _isInitializing = true;
        MainDictationHotkey = ResolveMainDictationHotkey(settings.Current);
        _isInitializing = false;

        _modelManager.PluginManager.PluginStateChanged += (_, _) =>
            Application.Current?.Dispatcher.Invoke(RefreshModels);
        _modelManager.PropertyChanged += OnModelManagerChanged;
        _dictation.PropertyChanged += OnDictationPropertyChanged;

        RefreshMicrophones();
        _ = LoadPluginsAsync();
    }

    public bool IsFirstStep => CurrentStep == 0;
    public bool IsFinalStep => CurrentStep == 3;
    public string PrimaryActionLabel => IsFinalStep ? Loc.Instance["Welcome.GetStarted"] : Loc.Instance["Welcome.Next"];
    public string MainDictationHotkeyDisplay => string.IsNullOrWhiteSpace(MainDictationHotkey)
        ? Loc.Instance["Hotkey.ClickToAssign"]
        : MainDictationHotkey;
    public bool HasConfiguredMainHotkey => !string.IsNullOrWhiteSpace(MainDictationHotkey);
    public bool HasReadyEngine =>
        !string.IsNullOrWhiteSpace(SelectedModelId)
        && _modelManager.GetStatus(SelectedModelId).Type == ModelStatusType.Ready;
    public RegistryPluginItemViewModel? RecommendedLocalPlugin => Plugins.FirstOrDefault(p => p.Id == LocalPluginId);
    public RegistryPluginItemViewModel? RecommendedCloudPlugin => Plugins.FirstOrDefault(p => p.Id == GroqPluginId);
    public bool IsLocalRecommendationInstalled => LocalEngine is not null;
    public bool IsCloudRecommendationInstalled => CloudEngine is not null;
    public bool IsCloudRecommendationConfigured => CloudEngine?.IsConfigured ?? false;
    public bool IsLocalRecommendationSelected => SelectedModelId == ParakeetModelId;
    public bool IsCloudRecommendationSelected => SelectedModelId?.StartsWith($"plugin:{GroqPluginId}:") == true;
    public string SelectedModelActionLabel => GetSelectedModelActionLabel();
    public bool CanApplySelectedModel =>
        !string.IsNullOrWhiteSpace(SelectedModelId)
        && !IsDownloading
        && !(SelectedModelId == _modelManager.ActiveModelId
             && _modelManager.GetStatus(SelectedModelId).Type == ModelStatusType.Ready);
    public string LocalRecommendationStatus => GetLocalRecommendationStatus();
    public string CloudRecommendationStatus => GetCloudRecommendationStatus();
    public bool CanTryItOut => HasReadyEngine && HasConfiguredMainHotkey;
    public bool TrialIsRecording => _dictation.State == DictationState.Recording;
    public bool TrialIsProcessing =>
        _dictation.State == DictationState.Processing || _dictation.State == DictationState.Inserting;
    public bool TrialHasError => _dictation.State == DictationState.Error;
    public string TrialStatusText => _dictation.StatusText;
    public bool ShowTrialInlineStatus =>
        !TrialSuccess
        && !TrialIsRecording
        && !TrialIsProcessing
        && !string.IsNullOrWhiteSpace(TrialInlineStatus);
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

        if (_isInitializing)
            return;

        PersistMainDictationHotkey(value);
    }

    partial void OnSelectedModelIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasReadyEngine));
        OnPropertyChanged(nameof(CanTryItOut));
        OnPropertyChanged(nameof(IsLocalRecommendationSelected));
        OnPropertyChanged(nameof(IsCloudRecommendationSelected));
        OnPropertyChanged(nameof(SelectedModelActionLabel));
        OnPropertyChanged(nameof(CanApplySelectedModel));
    }

    private async Task LoadPluginsAsync()
    {
        IsLoadingPlugins = true;

        try
        {
            var registryPlugins = await _registry.FetchRegistryAsync();

            Application.Current?.Dispatcher.Invoke(() =>
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
            foreach (var model in engine.TranscriptionModels)
            {
                var fullId = ModelManagerService.GetPluginModelId(engine.PluginId, model.Id);
                var name = $"{model.DisplayName} ({model.SizeDescription})";
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
    private void SelectRecommendedCloud()
    {
        SelectedModelId = AvailableModels.FirstOrDefault(m => m.FullModelId == PreferredGroqModelId)?.FullModelId
                          ?? AvailableModels.FirstOrDefault(m => m.FullModelId.StartsWith($"plugin:{GroqPluginId}:"))?.FullModelId
                          ?? SelectedModelId;
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
            Finish();
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
        foreach (var (number, name) in AudioRecordingService.GetAvailableDevices())
            Microphones.Add(new MicrophoneItem(number, name));
    }

    private void Finish()
    {
        StopMicTest();
        PersistMainDictationHotkey(MainDictationHotkey);

        Completed?.Invoke(this, EventArgs.Empty);
    }

    public void Cleanup()
    {
        StopMicTest();
        _modelManager.PropertyChanged -= OnModelManagerChanged;
        _dictation.PropertyChanged -= OnDictationPropertyChanged;
    }

    private static string ResolveMainDictationHotkey(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(HotkeyParser.Normalize(settings.PushToTalkHotkey))
            ? HotkeyParser.Normalize(settings.PushToTalkHotkey)
            : HotkeyParser.Normalize(settings.ToggleHotkey);

    private void PersistMainDictationHotkey(string? hotkey)
    {
        var normalizedHotkey = HotkeyParser.Normalize(hotkey);
        var current = _settings.Current;

        if (current.PushToTalkHotkey == normalizedHotkey && current.ToggleHotkey == normalizedHotkey)
            return;

        _settings.Save(current with
        {
            PushToTalkHotkey = normalizedHotkey,
            ToggleHotkey = normalizedHotkey
        });
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
        OnPropertyChanged(nameof(LocalRecommendationStatus));
        OnPropertyChanged(nameof(CloudRecommendationStatus));
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

        var status = _modelManager.GetStatus(SelectedModelId);
        if (status.Type == ModelStatusType.Ready)
        {
            return SelectedModelId == _modelManager.ActiveModelId
                ? Loc.Instance["Welcome.Ready"]
                : Loc.Instance["Welcome.UseSelectedModel"];
        }

        return Loc.Instance["Welcome.DownloadAndActivate"];
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
