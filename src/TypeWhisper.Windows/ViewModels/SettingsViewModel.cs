using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides settings view model behavior.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;
    private readonly ApiServerController _apiServer;
    private readonly CliInstallService _cliInstall;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly Action<Action> _dispatchToUi;
    private readonly object _previewLevelLock = new();
    private float _pendingPreviewLevel;
    private bool _previewLevelDispatchQueued;

    [ObservableProperty] private string _toggleHotkey = "";
    [ObservableProperty] private string _pushToTalkHotkey = "";
    [ObservableProperty] private string _toggleOnlyHotkey = "";
    [ObservableProperty] private string _holdOnlyHotkey = "";
    [ObservableProperty] private string _recentTranscriptionsHotkey = "";
    [ObservableProperty] private string _copyLastTranscriptionHotkey = "";
    [ObservableProperty] private string _workflowPaletteHotkey = "";
    [ObservableProperty] private string _recorderToggleHotkey = "";
    [ObservableProperty] private string _newMainDictationHotkey = "";
    [ObservableProperty] private string _newToggleOnlyHotkey = "";
    [ObservableProperty] private string _newHoldOnlyHotkey = "";
    [ObservableProperty] private string _newRecentTranscriptionsHotkey = "";
    [ObservableProperty] private string _newCopyLastTranscriptionHotkey = "";
    [ObservableProperty] private string _newWorkflowPaletteHotkey = "";
    [ObservableProperty] private string _newRecorderToggleHotkey = "";
    [ObservableProperty] private string _shortcutsError = "";
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _autoPaste = true;
    [ObservableProperty] private RecordingMode _mode = RecordingMode.Toggle;
    [ObservableProperty] private bool _whisperModeEnabled;
    [ObservableProperty] private bool _soundFeedbackEnabled = true;
    [ObservableProperty] private bool _audioDuckingEnabled;
    [ObservableProperty] private float _audioDuckingLevel = 0.2f;
    [ObservableProperty] private bool _pauseMediaDuringRecording;
    [ObservableProperty] private bool _transcribeShortQuietClipsAggressively;
    [ObservableProperty] private bool _transcriptionNumberNormalizationEnabled = true;
    [ObservableProperty] private IndicatorStyle _indicatorStyle = IndicatorStyle.StatusIsland;
    [ObservableProperty] private bool _liveTranscriptionEnabled = true;
    [ObservableProperty] private bool _onlineAsrBatchLiveTranscriptionEnabled;
    [ObservableProperty] private double _liveTranscriptionFontSize = AppSettings.DefaultLiveTranscriptionFontSize;
    [ObservableProperty] private OverlayPosition _overlayPosition = OverlayPosition.Bottom;
    [ObservableProperty] private double _previewBubbleAutoHideSeconds = AppSettings.DefaultPreviewBubbleAutoHideMilliseconds / 1000d;
    [ObservableProperty] private HistoryRetentionOption? _selectedHistoryRetentionOption;
    [ObservableProperty] private string _transcriptionTask = "transcribe";
    [ObservableProperty] private bool _quickTranslationModeEnabled;
    [ObservableProperty] private int? _selectedMicrophoneDevice;
    [ObservableProperty] private float _previewLevel;
    [ObservableProperty] private bool _saveToHistoryEnabled = true;
    [ObservableProperty] private bool _spokenFeedbackEnabled;
    [ObservableProperty] private bool _memoryEnabled;
    [ObservableProperty] private int _autoUnloadMinutes;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private bool _apiServerEnabled;
    [ObservableProperty] private int _apiServerPort = 8978;
    [ObservableProperty] private bool _apiServerRequiresAuthentication;
    [ObservableProperty] private OverlayWidget _overlayLeftWidget = OverlayWidget.Waveform;
    [ObservableProperty] private OverlayWidget _overlayRightWidget = OverlayWidget.Timer;
    [ObservableProperty] private string? _uiLanguage;
    [ObservableProperty] private string _apiServerStatusText = "";
    [ObservableProperty] private string _apiServerErrorText = "";
    [ObservableProperty] private bool _apiServerHasError;
    [ObservableProperty] private string _cliStatusText = "";
    [ObservableProperty] private string _cliBundledPathText = "";
    [ObservableProperty] private bool _cliBundledAvailable;
    [ObservableProperty] private bool _cliInstalled;
    [ObservableProperty] private string _selectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
    [ObservableProperty] private string? _selectedSpokenFeedbackVoiceId;

    /// <summary>
    /// Gets the translation target options.
    /// </summary>
    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    /// <summary>
    /// Gets the history retention options.
    /// </summary>
    public ObservableCollection<HistoryRetentionOption> HistoryRetentionOptions { get; } = [];
    /// <summary>
    /// Gets the curl examples.
    /// </summary>
    public ObservableCollection<CommandExample> CurlExamples { get; } = [];
    /// <summary>
    /// Gets the cli examples.
    /// </summary>
    public ObservableCollection<CommandExample> CliExamples { get; } = [];
    /// <summary>
    /// Gets the spoken feedback providers.
    /// </summary>
    public ObservableCollection<TtsProviderOption> SpokenFeedbackProviders { get; } = [];
    /// <summary>
    /// Gets the spoken feedback voices.
    /// </summary>
    public ObservableCollection<TtsVoiceOption> SpokenFeedbackVoices { get; } = [];

    /// <summary>
    /// Gets the configured main dictation hotkeys.
    /// </summary>
    public ObservableCollection<string> MainDictationHotkeys { get; } = [];

    /// <summary>
    /// Gets hotkeys that force toggle recording mode.
    /// </summary>
    public ObservableCollection<string> ToggleOnlyHotkeys { get; } = [];

    /// <summary>
    /// Gets hotkeys that force hold-to-record mode.
    /// </summary>
    public ObservableCollection<string> HoldOnlyHotkeys { get; } = [];

    /// <summary>
    /// Gets hotkeys that open the recent transcriptions palette.
    /// </summary>
    public ObservableCollection<string> RecentTranscriptionsHotkeys { get; } = [];

    /// <summary>
    /// Gets hotkeys that copy the most recent transcription.
    /// </summary>
    public ObservableCollection<string> CopyLastTranscriptionHotkeys { get; } = [];

    /// <summary>
    /// Gets hotkeys that open the workflow palette.
    /// </summary>
    public ObservableCollection<string> WorkflowPaletteHotkeys { get; } = [];
    /// <summary>
    /// Gets hotkeys that toggle the recorder.
    /// </summary>
    public ObservableCollection<string> RecorderToggleHotkeys { get; } = [];

    private static IReadOnlyList<TranslationTargetOption> LocalizeTranslationOptions(IReadOnlyList<TranslationTargetOption> options) =>
        options.Select(o => o.DisplayName switch
        {
            "Keine Übersetzung" => o with { DisplayName = Loc.Instance["Translation.None"] },
            "Globale Einstellung" => o with { DisplayName = Loc.Instance["Translation.GlobalSetting"] },
            _ => o
        }).ToList();

    private static IReadOnlyList<OverlayWidgetOption> BuildWidgetOptions() =>
    [
        new(OverlayWidget.None, Loc.Instance["Widget.None"]),
        new(OverlayWidget.Indicator, Loc.Instance["Widget.Indicator"]),
        new(OverlayWidget.Timer, Loc.Instance["Widget.Timer"]),
        new(OverlayWidget.Waveform, Loc.Instance["Widget.Waveform"]),
        new(OverlayWidget.Clock, Loc.Instance["Widget.Clock"]),
        new(OverlayWidget.Profile, Loc.Instance["Widget.Workflow"]),
        new(OverlayWidget.HotkeyMode, Loc.Instance["Widget.HotkeyMode"]),
        new(OverlayWidget.AppName, Loc.Instance["Widget.AppName"]),
    ];

    private static IReadOnlyList<HistoryRetentionOption> BuildHistoryRetentionOptions() =>
    [
        new(HistoryRetentionMode.Duration, 60, Loc.Instance["Advanced.Retention1Hour"]),
        new(HistoryRetentionMode.Duration, 24 * 60, Loc.Instance["Advanced.Retention1Day"]),
        new(HistoryRetentionMode.Duration, 7 * 24 * 60, Loc.Instance["Advanced.Retention7"]),
        new(HistoryRetentionMode.Duration, 30 * 24 * 60, Loc.Instance["Advanced.Retention30"]),
        new(HistoryRetentionMode.Duration, 90 * 24 * 60, Loc.Instance["Advanced.Retention90"]),
        new(HistoryRetentionMode.Duration, 365 * 24 * 60, Loc.Instance["Advanced.Retention365"]),
        new(HistoryRetentionMode.Forever, null, Loc.Instance["Advanced.RetentionForever"]),
        new(HistoryRetentionMode.UntilAppCloses, null, Loc.Instance["Advanced.RetentionUntilAppCloses"])
    ];

    /// <summary>
    /// Gets the microphones.
    /// </summary>
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];
    /// <summary>
    /// Gets the preferred microphones in fallback order.
    /// </summary>
    public ObservableCollection<MicrophonePriorityListItem> MicrophonePriorityItems { get; } = [];
    /// <summary>
    /// Gets whether microphone priority items exist.
    /// </summary>
    public bool HasMicrophonePriorityItems => MicrophonePriorityItems.Count > 0;
    /// <summary>
    /// Gets the widget options.
    /// </summary>
    public ObservableCollection<OverlayWidgetOption> WidgetOptions { get; } = [];

    private MicrophoneItem? _selectedMicrophoneItem;
    private bool _isSyncingMicrophoneSelection;
    private bool _isApplyingMicrophoneItemSelection;
    private IReadOnlyList<MicrophonePriorityItem> _microphonePriorityList = [];

    /// <summary>
    /// Gets the selected microphone item.
    /// </summary>
    public MicrophoneItem? SelectedMicrophoneItem
    {
        get => _selectedMicrophoneItem;
        set
        {
            if (!SetProperty(ref _selectedMicrophoneItem, value))
                return;

            NotifyMicrophonePriorityCommandStates();

            if (_isSyncingMicrophoneSelection || _isLoading)
                return;

            if (_microphonePriorityList.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(value?.Id))
                    SyncSelectedMicrophoneItem();
                return;
            }

            var priorityChanged = UpdateMicrophonePriorityFromSelectedItem(value);
            var previousDevice = SelectedMicrophoneDevice;
            _isApplyingMicrophoneItemSelection = true;
            try
            {
                SelectedMicrophoneDevice = value?.DeviceNumber;
            }
            finally
            {
                _isApplyingMicrophoneItemSelection = false;
            }
            if (priorityChanged && previousDevice == SelectedMicrophoneDevice)
                Save();
        }
    }

    /// <summary>
    /// Gets the preview bubble auto hide seconds text.
    /// </summary>
    public string PreviewBubbleAutoHideSecondsText =>
        Loc.Instance.GetString("Appearance.AutoHideSecondsFormat", PreviewBubbleAutoHideSeconds);
    /// <summary>
    /// Gets the live transcription font size text.
    /// </summary>
    public string LiveTranscriptionFontSizeText =>
        Loc.Instance.GetString("Appearance.LiveTextSizeFormat", LiveTranscriptionFontSize);

    private bool _isLoading;
    private bool _isSavingSettings;
    private bool _isSyncingShortcutHotkeys;

    partial void OnUiLanguageChanged(string? value)
    {
        if (_isLoading) return;
        Loc.Instance.CurrentLanguage = value ?? Loc.Instance.DetectSystemLanguage();
    }

    partial void OnSelectedMicrophoneDeviceChanged(int? value)
    {
        if (_isLoading) return;
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        _audio.SetMicrophoneDevice(value);
        StopMicrophonePreview();
        if (!_isApplyingMicrophoneItemSelection)
            SyncSelectedMicrophoneItem();
        NotifyMicrophonePriorityCommandStates();
    }

    partial void OnSelectedSpokenFeedbackProviderIdChanged(string value)
    {
        if (_isLoading) return;
        RefreshSpokenFeedbackVoices();
    }

    partial void OnSelectedSpokenFeedbackVoiceIdChanged(string? value)
    {
        if (_isLoading) return;
        _speechFeedback.SelectVoice(SelectedSpokenFeedbackProviderId, value);
    }

    partial void OnPreviewBubbleAutoHideSecondsChanged(double value) =>
        OnPropertyChanged(nameof(PreviewBubbleAutoHideSecondsText));

    partial void OnLiveTranscriptionFontSizeChanged(double value) =>
        OnPropertyChanged(nameof(LiveTranscriptionFontSizeText));

    partial void OnQuickTranslationModeEnabledChanged(bool value)
    {
        if (_isLoading) return;

        if (value)
        {
            if (string.IsNullOrWhiteSpace(TranslationTargetLanguage))
            {
                TranslationTargetLanguage =
                    _settings.Current.LastTranslationTargetLanguage
                    ?? DefaultQuickTranslationTargetLanguage();
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(TranslationTargetLanguage))
            TranslationTargetLanguage = null;
    }

    partial void OnTranslationTargetLanguageChanged(string? value)
    {
        if (_isLoading) return;

        SetQuickTranslationModeSilently(!string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// Initializes a new instance of the SettingsViewModel class.
    /// </summary>
    public SettingsViewModel(
        ISettingsService settings,
        AudioRecordingService audio,
        ApiServerController apiServer,
        CliInstallService cliInstall,
        SpeechFeedbackService speechFeedback,
        Action<Action>? dispatchToUi = null)
    {
        _settings = settings;
        _audio = audio;
        _apiServer = apiServer;
        _cliInstall = cliInstall;
        _speechFeedback = speechFeedback;
        _dispatchToUi = dispatchToUi ?? CreateUiDispatcher();
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _apiServer.StateChanged += OnApiServerStateChanged;
        _speechFeedback.ProvidersChanged += OnTtsProvidersChanged;
        _audio.DevicesChanged += OnAudioDevicesChanged;
        RegisterShortcutHotkeyCollections();

        _isLoading = true;
        RefreshLocalizedCollections(refreshMicrophones: false);
        LoadFromSettings(_settings.Current);
        RefreshSpokenFeedbackProviders();
        AutostartEnabled = StartupService.IsEnabled;
        RefreshMicrophones();
        RefreshApiServerStatus();
        RefreshCliState();
        RefreshApiExamples();
        _isLoading = false;

        _settings.SettingsChanged += OnSettingsChanged;

        PropertyChanged += (_, args) =>
        {
            if (_isLoading) return;
            if (_isSyncingShortcutHotkeys) return;
            if (IsTransientSettingsProperty(args.PropertyName)) return;

            if (args.PropertyName == nameof(SelectedMicrophoneItem))
                return;

            if (args.PropertyName == nameof(AutostartEnabled))
            {
                ApplyAutostartSetting();
                return;
            }

            if (args.PropertyName == nameof(SelectedSpokenFeedbackVoiceId))
            {
                if (IsBuiltInSpokenFeedbackProviderSelected())
                    Save();
                return;
            }

            Save();
        };
    }

    [RelayCommand]
    private void RefreshMicrophones()
    {
        var selectedDevice = SelectedMicrophoneDevice;
        Microphones.Clear();
        Microphones.Add(new MicrophoneItem(null, Loc.Instance["Microphone.Default"]));
        var availableDevices = _audio.GetAvailableInputDeviceInfos();
        foreach (var device in availableDevices)
        {
            Microphones.Add(new MicrophoneItem(device.DeviceNumber, device.Name, device.Id));
        }

        MigrateLegacyMicrophoneSelection(availableDevices);
        selectedDevice = SelectedMicrophoneDevice;

        var hasAvailablePriorityItem = _microphonePriorityList.Any(priorityItem =>
            availableDevices.Any(device => MicrophoneMatches(device, priorityItem)));
        var selectedPriorityItem = _microphonePriorityList.FirstOrDefault();
        if (_microphonePriorityList.Count > 0 && !hasAvailablePriorityItem && selectedPriorityItem is not null)
        {
            Microphones.Add(new MicrophoneItem(
                selectedDevice,
                Loc.Instance["Microphone.Disconnected"],
                selectedPriorityItem.Id));
        }

        if (_microphonePriorityList.Count == 0
            && selectedDevice is int selectedDeviceNumber
            && availableDevices.All(device => device.DeviceNumber != selectedDeviceNumber))
        {
            Microphones.Add(new MicrophoneItem(selectedDeviceNumber, Loc.Instance["Microphone.Disconnected"]));
        }

        SyncSelectedMicrophoneItem();
        NotifyMicrophonePriorityCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanAddMicrophonePriorityItem))]
    private void AddMicrophonePriorityItem()
    {
        var item = SelectedMicrophoneItem;
        if (string.IsNullOrWhiteSpace(item?.Id))
            return;

        if (_microphonePriorityList.Any(existing =>
                string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var updated = _microphonePriorityList
            .Append(new MicrophonePriorityItem(item.Id, item.Name))
            .ToList();

        if (SetMicrophonePriorityList(updated))
            Save();
    }

    private bool CanAddMicrophonePriorityItem() =>
        !string.IsNullOrWhiteSpace(SelectedMicrophoneItem?.Id);

    [RelayCommand]
    private void ReorderMicrophonePriorityItem(MicrophonePriorityReorderRequest? request)
    {
        if (request is null)
            return;

        var sourceIndex = IndexOfMicrophonePriorityItem(request.Source);
        var targetIndex = IndexOfMicrophonePriorityItem(request.Target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return;

        var updated = _microphonePriorityList.ToList();
        var moved = updated[sourceIndex];
        updated.RemoveAt(sourceIndex);
        updated.Insert(targetIndex > updated.Count ? updated.Count : targetIndex, moved);

        if (SetMicrophonePriorityList(updated))
            Save();
    }

    [RelayCommand]
    private void RemoveMicrophonePriorityItem(MicrophonePriorityListItem? item)
    {
        var index = IndexOfMicrophonePriorityItem(item);
        if (index < 0)
            return;

        var updated = _microphonePriorityList.ToList();
        updated.RemoveAt(index);
        if (SetMicrophonePriorityList(updated))
            Save();
    }

    [RelayCommand]
    private void CopyCommandExample(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        try
        {
            Clipboard.SetText(command);
        }
        catch (COMException)
        {
            // Clipboard may be temporarily locked by another process.
        }
        catch (ExternalException)
        {
            // Clipboard may be temporarily locked by another process.
        }
    }

    [RelayCommand]
    private void RefreshCliState() => ApplyCliState(_cliInstall.GetState());

    [RelayCommand]
    private void AddMainDictationHotkey(string? hotkey = null) =>
        AddShortcutHotkey(MainDictationHotkeys, hotkey ?? NewMainDictationHotkey, value => NewMainDictationHotkey = value);

    [RelayCommand]
    private void RemoveMainDictationHotkey(string? hotkey) =>
        RemoveShortcutHotkey(MainDictationHotkeys, hotkey);

    [RelayCommand]
    private void AddToggleOnlyHotkey(string? hotkey = null) =>
        AddShortcutHotkey(ToggleOnlyHotkeys, hotkey ?? NewToggleOnlyHotkey, value => NewToggleOnlyHotkey = value);

    [RelayCommand]
    private void RemoveToggleOnlyHotkey(string? hotkey) =>
        RemoveShortcutHotkey(ToggleOnlyHotkeys, hotkey);

    [RelayCommand]
    private void AddHoldOnlyHotkey(string? hotkey = null) =>
        AddShortcutHotkey(HoldOnlyHotkeys, hotkey ?? NewHoldOnlyHotkey, value => NewHoldOnlyHotkey = value);

    [RelayCommand]
    private void RemoveHoldOnlyHotkey(string? hotkey) =>
        RemoveShortcutHotkey(HoldOnlyHotkeys, hotkey);

    [RelayCommand]
    private void AddRecentTranscriptionsHotkey(string? hotkey = null) =>
        AddShortcutHotkey(
            RecentTranscriptionsHotkeys,
            hotkey ?? NewRecentTranscriptionsHotkey,
            value => NewRecentTranscriptionsHotkey = value);

    [RelayCommand]
    private void RemoveRecentTranscriptionsHotkey(string? hotkey) =>
        RemoveShortcutHotkey(RecentTranscriptionsHotkeys, hotkey);

    [RelayCommand]
    private void AddCopyLastTranscriptionHotkey(string? hotkey = null) =>
        AddShortcutHotkey(
            CopyLastTranscriptionHotkeys,
            hotkey ?? NewCopyLastTranscriptionHotkey,
            value => NewCopyLastTranscriptionHotkey = value);

    [RelayCommand]
    private void RemoveCopyLastTranscriptionHotkey(string? hotkey) =>
        RemoveShortcutHotkey(CopyLastTranscriptionHotkeys, hotkey);

    [RelayCommand]
    private void AddWorkflowPaletteHotkey(string? hotkey = null) =>
        AddShortcutHotkey(
            WorkflowPaletteHotkeys,
            hotkey ?? NewWorkflowPaletteHotkey,
            value => NewWorkflowPaletteHotkey = value);

    [RelayCommand]
    private void RemoveWorkflowPaletteHotkey(string? hotkey) =>
        RemoveShortcutHotkey(WorkflowPaletteHotkeys, hotkey);

    [RelayCommand]
    private void AddRecorderToggleHotkey(string? hotkey = null) =>
        AddShortcutHotkey(
            RecorderToggleHotkeys,
            hotkey ?? NewRecorderToggleHotkey,
            value => NewRecorderToggleHotkey = value);

    [RelayCommand]
    private void RemoveRecorderToggleHotkey(string? hotkey) =>
        RemoveShortcutHotkey(RecorderToggleHotkeys, hotkey);

    [RelayCommand]
    private void InstallCli()
    {
        try
        {
            ApplyCliState(_cliInstall.Install());
        }
        catch (InvalidOperationException ex)
        {
            CliStatusText = ex.Message;
        }
        catch (IOException ex)
        {
            CliStatusText = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            CliStatusText = ex.Message;
        }
    }

    /// <summary>
    /// Starts microphone preview.
    /// </summary>
    public void StartMicrophonePreview()
    {
        _audio.PreviewLevelChanged -= OnPreviewLevelChanged;
        if (!_audio.HasDevice) return;
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        _audio.SetMicrophoneDevice(SelectedMicrophoneDevice);
        _audio.StartPreview(SelectedMicrophoneDevice);
        _audio.PreviewLevelChanged += OnPreviewLevelChanged;
    }

    /// <summary>
    /// Stops microphone preview.
    /// </summary>
    public void StopMicrophonePreview()
    {
        _audio.PreviewLevelChanged -= OnPreviewLevelChanged;
        _audio.StopPreview();
        lock (_previewLevelLock)
        {
            _pendingPreviewLevel = 0;
        }
        PreviewLevel = 0;
    }

    private void OnPreviewLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        var level = Math.Min(e.RmsLevel * 5f, 1f); // Scale for visibility.
        var shouldDispatch = false;
        lock (_previewLevelLock)
        {
            _pendingPreviewLevel = level;
            if (!_previewLevelDispatchQueued)
            {
                _previewLevelDispatchQueued = true;
                shouldDispatch = true;
            }
        }

        if (shouldDispatch)
            _dispatchToUi(ApplyPendingPreviewLevel);
    }

    private void ApplyPendingPreviewLevel()
    {
        float level;
        lock (_previewLevelLock)
        {
            level = _pendingPreviewLevel;
            _previewLevelDispatchQueued = false;
        }

        PreviewLevel = level;
    }

    [RelayCommand]
    private void Save()
    {
        var mainDictationHotkeys = NormalizeHotkeyList(MainDictationHotkeys);
        var toggleOnlyHotkeys = NormalizeHotkeyList(ToggleOnlyHotkeys);
        var holdOnlyHotkeys = NormalizeHotkeyList(HoldOnlyHotkeys);
        var recentTranscriptionsHotkeys = NormalizeHotkeyList(RecentTranscriptionsHotkeys);
        var copyLastTranscriptionHotkeys = NormalizeHotkeyList(CopyLastTranscriptionHotkeys);
        var workflowPaletteHotkeys = NormalizeHotkeyList(WorkflowPaletteHotkeys);
        var recorderToggleHotkeys = NormalizeHotkeyList(RecorderToggleHotkeys);
        var mainDictationHotkey = FirstOrEmpty(mainDictationHotkeys);
        var selectedVoiceId = SpeechFeedbackService.IsDefaultVoiceOptionId(SelectedSpokenFeedbackVoiceId)
            ? null
            : SelectedSpokenFeedbackVoiceId;

        var updated = _settings.Current with
        {
            ToggleHotkey = mainDictationHotkey,
            PushToTalkHotkey = mainDictationHotkey,
            MainDictationHotkeys = mainDictationHotkeys,
            Language = Language,
            AutoPaste = AutoPaste,
            Mode = Mode,
            WhisperModeEnabled = WhisperModeEnabled,
            SoundFeedbackEnabled = SoundFeedbackEnabled,
            TranscribeShortQuietClipsAggressively = TranscribeShortQuietClipsAggressively,
            TranscriptionNumberNormalizationEnabled = TranscriptionNumberNormalizationEnabled,
            IndicatorStyle = IndicatorStyle,
            LiveTranscriptionEnabled = LiveTranscriptionEnabled,
            OnlineAsrBatchLiveTranscriptionEnabled = OnlineAsrBatchLiveTranscriptionEnabled,
            LiveTranscriptionFontSize = AppSettings.NormalizeLiveTranscriptionFontSize(LiveTranscriptionFontSize),
            OverlayPosition = OverlayPosition,
            PreviewBubbleAutoHideMilliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                (int)Math.Round(PreviewBubbleAutoHideSeconds * 1000, MidpointRounding.AwayFromZero)),
            HistoryRetentionMode = SelectedHistoryRetentionOption?.Mode ?? AppSettings.Default.HistoryRetentionMode,
            HistoryRetentionMinutes = SelectedHistoryRetentionOption?.Minutes ?? AppSettings.Default.HistoryRetentionMinutes,
            TranscriptionTask = TranscriptionTask,
            SelectedMicrophoneDevice = SelectedMicrophoneDevice,
            MicrophonePriorityList = _microphonePriorityList,
            TranslationTargetLanguage = NormalizeTranslationTarget(TranslationTargetLanguage),
            LastTranslationTargetLanguage = ResolveLastTranslationTarget(),
            ApiServerEnabled = ApiServerEnabled,
            ApiServerPort = ApiServerPort,
            ApiServerRequiresAuthentication = ApiServerRequiresAuthentication,
            ToggleOnlyHotkeys = toggleOnlyHotkeys,
            ToggleOnlyHotkey = FirstOrEmpty(toggleOnlyHotkeys),
            HoldOnlyHotkeys = holdOnlyHotkeys,
            HoldOnlyHotkey = FirstOrEmpty(holdOnlyHotkeys),
            RecentTranscriptionsHotkeys = recentTranscriptionsHotkeys,
            RecentTranscriptionsHotkey = FirstOrEmpty(recentTranscriptionsHotkeys),
            CopyLastTranscriptionHotkeys = copyLastTranscriptionHotkeys,
            CopyLastTranscriptionHotkey = FirstOrEmpty(copyLastTranscriptionHotkeys),
            WorkflowPaletteHotkeys = workflowPaletteHotkeys,
            WorkflowPaletteHotkey = FirstOrEmpty(workflowPaletteHotkeys),
            RecorderToggleHotkeys = recorderToggleHotkeys,
            RecorderToggleHotkey = FirstOrEmpty(recorderToggleHotkeys),
            AudioDuckingEnabled = AudioDuckingEnabled,
            AudioDuckingLevel = AudioDuckingLevel,
            PauseMediaDuringRecording = PauseMediaDuringRecording,
            OverlayLeftWidget = OverlayLeftWidget,
            OverlayRightWidget = OverlayRightWidget,
            SaveToHistoryEnabled = SaveToHistoryEnabled,
            SpokenFeedbackEnabled = SpokenFeedbackEnabled,
            SpokenFeedbackProviderId = string.IsNullOrWhiteSpace(SelectedSpokenFeedbackProviderId)
                ? AppSettings.DefaultSpokenFeedbackProviderId
                : SelectedSpokenFeedbackProviderId,
            SpokenFeedbackVoiceId = string.Equals(
                SelectedSpokenFeedbackProviderId,
                AppSettings.DefaultSpokenFeedbackProviderId,
                StringComparison.OrdinalIgnoreCase)
                    ? selectedVoiceId
                    : _settings.Current.SpokenFeedbackVoiceId,
            MemoryEnabled = MemoryEnabled,
            ModelAutoUnloadSeconds = AutoUnloadMinutes * 60,
            UiLanguage = UiLanguage
        };

        _isSavingSettings = true;
        try
        {
            _settings.Save(updated);
        }
        finally
        {
            _isSavingSettings = false;
        }
    }

    private void ApplyAutostartSetting()
    {
        var requestedAutostartEnabled = AutostartEnabled;
        var currentAutostartEnabled = StartupService.IsEnabled;

        if (currentAutostartEnabled != requestedAutostartEnabled)
            StartupService.SetEnabled(requestedAutostartEnabled);

        var actualAutostartEnabled = StartupService.IsEnabled;
        if (actualAutostartEnabled == requestedAutostartEnabled)
            return;

        _isLoading = true;
        AutostartEnabled = actualAutostartEnabled;
        _isLoading = false;
    }

    private void LoadFromSettings(AppSettings s)
    {
        var mainDictationHotkeys = NormalizeHotkeyList(s.GetMainDictationHotkeys());
        var toggleOnlyHotkeys = NormalizeHotkeyList(s.GetToggleOnlyHotkeys());
        var holdOnlyHotkeys = NormalizeHotkeyList(s.GetHoldOnlyHotkeys());
        var recentTranscriptionsHotkeys = NormalizeHotkeyList(s.GetRecentTranscriptionsHotkeys());
        var copyLastTranscriptionHotkeys = NormalizeHotkeyList(s.GetCopyLastTranscriptionHotkeys());
        var workflowPaletteHotkeys = NormalizeHotkeyList(s.GetWorkflowPaletteHotkeys());
        var recorderToggleHotkeys = NormalizeHotkeyList(s.GetRecorderToggleHotkeys());
        var mainDictationHotkey = FirstOrEmpty(mainDictationHotkeys);

        ReplaceCollection(MainDictationHotkeys, mainDictationHotkeys);
        ReplaceCollection(ToggleOnlyHotkeys, toggleOnlyHotkeys);
        ReplaceCollection(HoldOnlyHotkeys, holdOnlyHotkeys);
        ReplaceCollection(RecentTranscriptionsHotkeys, recentTranscriptionsHotkeys);
        ReplaceCollection(CopyLastTranscriptionHotkeys, copyLastTranscriptionHotkeys);
        ReplaceCollection(WorkflowPaletteHotkeys, workflowPaletteHotkeys);
        ReplaceCollection(RecorderToggleHotkeys, recorderToggleHotkeys);

        ToggleHotkey = mainDictationHotkey;
        PushToTalkHotkey = mainDictationHotkey;
        Language = s.Language;
        AutoPaste = s.AutoPaste;
        Mode = s.Mode;
        WhisperModeEnabled = s.WhisperModeEnabled;
        SoundFeedbackEnabled = s.SoundFeedbackEnabled;
        TranscribeShortQuietClipsAggressively = s.TranscribeShortQuietClipsAggressively;
        TranscriptionNumberNormalizationEnabled = s.TranscriptionNumberNormalizationEnabled;
        IndicatorStyle = s.IndicatorStyle;
        LiveTranscriptionEnabled = s.LiveTranscriptionEnabled;
        OnlineAsrBatchLiveTranscriptionEnabled = s.OnlineAsrBatchLiveTranscriptionEnabled;
        LiveTranscriptionFontSize = AppSettings.NormalizeLiveTranscriptionFontSize(s.LiveTranscriptionFontSize);
        OverlayPosition = s.OverlayPosition;
        PreviewBubbleAutoHideSeconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
            s.PreviewBubbleAutoHideMilliseconds) / 1000d;
        SelectedHistoryRetentionOption = MatchHistoryRetentionOption(s.HistoryRetentionMode, s.HistoryRetentionMinutes);
        TranscriptionTask = s.TranscriptionTask;
        _microphonePriorityList = s.NormalizeMicrophonePriorityList().MicrophonePriorityList;
        SyncMicrophonePriorityItems();
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        SelectedMicrophoneDevice = s.SelectedMicrophoneDevice;
        _audio.SetMicrophoneDevice(SelectedMicrophoneDevice);
        TranslationTargetLanguage = s.TranslationTargetLanguage;
        QuickTranslationModeEnabled = !string.IsNullOrWhiteSpace(s.TranslationTargetLanguage);
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        ApiServerRequiresAuthentication = s.ApiServerRequiresAuthentication;
        ToggleOnlyHotkey = FirstOrEmpty(toggleOnlyHotkeys);
        HoldOnlyHotkey = FirstOrEmpty(holdOnlyHotkeys);
        RecentTranscriptionsHotkey = FirstOrEmpty(recentTranscriptionsHotkeys);
        CopyLastTranscriptionHotkey = FirstOrEmpty(copyLastTranscriptionHotkeys);
        WorkflowPaletteHotkey = FirstOrEmpty(workflowPaletteHotkeys);
        RecorderToggleHotkey = FirstOrEmpty(recorderToggleHotkeys);
        AudioDuckingEnabled = s.AudioDuckingEnabled;
        AudioDuckingLevel = s.AudioDuckingLevel;
        PauseMediaDuringRecording = s.PauseMediaDuringRecording;
        OverlayLeftWidget = s.OverlayLeftWidget;
        OverlayRightWidget = s.OverlayRightWidget;
        SaveToHistoryEnabled = s.SaveToHistoryEnabled;
        SpokenFeedbackEnabled = s.SpokenFeedbackEnabled;
        SelectedSpokenFeedbackProviderId = string.IsNullOrWhiteSpace(s.SpokenFeedbackProviderId)
            ? AppSettings.DefaultSpokenFeedbackProviderId
            : s.SpokenFeedbackProviderId;
        SelectedSpokenFeedbackVoiceId = s.SpokenFeedbackVoiceId;
        MemoryEnabled = s.MemoryEnabled;
        AutoUnloadMinutes = s.ModelAutoUnloadSeconds / 60;
        UiLanguage = s.UiLanguage;
        RefreshApiExamples();
        RefreshApiServerStatus();
    }

    private void RegisterShortcutHotkeyCollections()
    {
        MainDictationHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        ToggleOnlyHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        HoldOnlyHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        RecentTranscriptionsHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        CopyLastTranscriptionHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        WorkflowPaletteHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
        RecorderToggleHotkeys.CollectionChanged += OnShortcutHotkeysChanged;
    }

    private void OnShortcutHotkeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isLoading)
            return;

        ShortcutsError = "";
        SyncShortcutHotkeyPropertiesFromCollections();
        Save();
    }

    private void AddShortcutHotkey(
        ObservableCollection<string> target,
        string? hotkey,
        Action<string> setNewHotkey)
    {
        var normalized = HotkeyParser.Normalize(hotkey);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (GetAllShortcutHotkeys().Any(existing =>
                string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            ShortcutsError = Loc.Instance.GetString("Shortcuts.ValidationDuplicateFormat", normalized);
            setNewHotkey("");
            return;
        }

        target.Add(normalized);
        setNewHotkey("");
    }

    private void RemoveShortcutHotkey(ObservableCollection<string> target, string? hotkey)
    {
        var normalized = HotkeyParser.Normalize(hotkey);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var existing = target.FirstOrDefault(value =>
            string.Equals(HotkeyParser.Normalize(value), normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        target.Remove(existing);
    }

    private IEnumerable<string> GetAllShortcutHotkeys() =>
    [
        .. NormalizeHotkeyList(MainDictationHotkeys),
        .. NormalizeHotkeyList(ToggleOnlyHotkeys),
        .. NormalizeHotkeyList(HoldOnlyHotkeys),
        .. NormalizeHotkeyList(RecentTranscriptionsHotkeys),
        .. NormalizeHotkeyList(CopyLastTranscriptionHotkeys),
        .. NormalizeHotkeyList(WorkflowPaletteHotkeys),
        .. NormalizeHotkeyList(RecorderToggleHotkeys)
    ];

    private void SyncShortcutHotkeyPropertiesFromCollections()
    {
        _isSyncingShortcutHotkeys = true;
        try
        {
            var mainDictationHotkey = FirstOrEmpty(NormalizeHotkeyList(MainDictationHotkeys));
            ToggleHotkey = mainDictationHotkey;
            PushToTalkHotkey = mainDictationHotkey;
            ToggleOnlyHotkey = FirstOrEmpty(NormalizeHotkeyList(ToggleOnlyHotkeys));
            HoldOnlyHotkey = FirstOrEmpty(NormalizeHotkeyList(HoldOnlyHotkeys));
            RecentTranscriptionsHotkey = FirstOrEmpty(NormalizeHotkeyList(RecentTranscriptionsHotkeys));
            CopyLastTranscriptionHotkey = FirstOrEmpty(NormalizeHotkeyList(CopyLastTranscriptionHotkeys));
            WorkflowPaletteHotkey = FirstOrEmpty(NormalizeHotkeyList(WorkflowPaletteHotkeys));
            RecorderToggleHotkey = FirstOrEmpty(NormalizeHotkeyList(RecorderToggleHotkeys));
        }
        finally
        {
            _isSyncingShortcutHotkeys = false;
        }
    }

    private static bool IsTransientSettingsProperty(string? propertyName) =>
        propertyName is nameof(PreviewLevel)
            or nameof(NewMainDictationHotkey)
            or nameof(NewToggleOnlyHotkey)
            or nameof(NewHoldOnlyHotkey)
            or nameof(NewRecentTranscriptionsHotkey)
            or nameof(NewCopyLastTranscriptionHotkey)
            or nameof(NewWorkflowPaletteHotkey)
            or nameof(NewRecorderToggleHotkey)
            or nameof(ShortcutsError)
            or nameof(HasMicrophonePriorityItems);

    private static IReadOnlyList<string> NormalizeHotkeyList(IEnumerable<string?> values) =>
        values.Select(HotkeyParser.Normalize)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FirstOrEmpty(IReadOnlyList<string> values) =>
        values.Count == 0 ? "" : values[0];

    private void OnSettingsChanged(AppSettings updatedSettings)
    {
        if (_isSavingSettings)
            return;

        _dispatchToUi(() =>
        {
            _isLoading = true;
            LoadFromSettings(updatedSettings);
            RefreshSpokenFeedbackProviders();
            AutostartEnabled = StartupService.IsEnabled;
            RefreshApiServerStatus();
            _isLoading = false;
        });
    }

    private void OnApiServerStateChanged()
    {
        _dispatchToUi(RefreshApiServerStatus);
    }

    private void RefreshApiServerStatus()
    {
        var port = _apiServer.ActivePort ?? ApiServerPort;
        ApiServerStatusText = ApiServerEnabled
            ? _apiServer.IsRunning
                ? Loc.Instance.GetString("Advanced.ApiRunningFormat", port)
                : Loc.Instance["Advanced.ApiNotRunning"]
            : Loc.Instance["Advanced.ApiDisabled"];

        ApiServerErrorText = _apiServer.ErrorMessage ?? "";
        ApiServerHasError = !string.IsNullOrWhiteSpace(ApiServerErrorText);
    }

    private void RefreshApiExamples()
    {
        ReplaceCollection(CurlExamples, CliInstallService.BuildCurlExamples(ApiServerPort)
            .Select(command => new CommandExample(command))
            .ToList());
        ReplaceCollection(CliExamples, CliInstallService.BuildCliExamples(ApiServerPort)
            .Select(command => new CommandExample(command))
            .ToList());
    }

    private void ApplyCliState(CliInstallState state)
    {
        CliBundledAvailable = state.BundledCliAvailable;
        CliInstalled = state.Installed;
        CliStatusText = state.StatusText;
        CliBundledPathText = state.BundledPath is null
            ? Loc.Instance["Advanced.CliBundledMissing"]
            : Loc.Instance.GetString("Advanced.CliBundledPathFormat", state.BundledPath);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _dispatchToUi(() =>
        {
            _isLoading = true;
            RefreshLocalizedCollections();
            SelectedHistoryRetentionOption = MatchHistoryRetentionOption(
                _settings.Current.HistoryRetentionMode,
                _settings.Current.HistoryRetentionMinutes);
            OnPropertyChanged(nameof(PreviewBubbleAutoHideSecondsText));
            OnPropertyChanged(nameof(LiveTranscriptionFontSizeText));
            RefreshSpokenFeedbackProviders();
            _isLoading = false;
        });
    }

    private void RefreshLocalizedCollections(bool refreshMicrophones = true)
    {
        ReplaceCollection(TranslationTargetOptions, LocalizeTranslationOptions(TranslationModelInfo.GlobalTargetOptions));
        ReplaceCollection(HistoryRetentionOptions, BuildHistoryRetentionOptions());
        ReplaceCollection(WidgetOptions, BuildWidgetOptions());
        if (refreshMicrophones)
            RefreshMicrophones();
    }

    private string? ResolveLastTranslationTarget()
    {
        var target = NormalizeTranslationTarget(TranslationTargetLanguage);
        return target ?? _settings.Current.LastTranslationTargetLanguage;
    }

    private static string? NormalizeTranslationTarget(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void SetQuickTranslationModeSilently(bool enabled)
    {
        if (QuickTranslationModeEnabled == enabled)
            return;

        var wasLoading = _isLoading;
        _isLoading = true;
        QuickTranslationModeEnabled = enabled;
        _isLoading = wasLoading;
    }

    private static string DefaultQuickTranslationTargetLanguage() =>
        TranslationModelInfo.SupportedLanguages.Any(language => language.Code == "en")
            ? "en"
            : TranslationModelInfo.SupportedLanguages.FirstOrDefault()?.Code ?? "en";

    private void OnTtsProvidersChanged(object? sender, EventArgs e)
    {
        _dispatchToUi(() =>
        {
            var wasLoading = _isLoading;
            _isLoading = true;
            RefreshSpokenFeedbackProviders();
            _isLoading = wasLoading;
        });
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        _dispatchToUi(HandleAudioDevicesChanged);
    }

    private static Action<Action> CreateUiDispatcher()
    {
        var dispatcher = CaptureActiveDispatcher();
        return action => DispatchToUi(dispatcher, action);
    }

    private static Dispatcher? CaptureActiveDispatcher()
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished
            ? null
            : dispatcher;
    }

    private static void DispatchToUi(Dispatcher? dispatcher, Action action)
    {
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
            _ = dispatcher.BeginInvoke(action);
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

    private void HandleAudioDevicesChanged()
    {
        var wasPreviewing = _audio.IsPreviewing;
        StopMicrophonePreview();
        RefreshMicrophones();
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        _audio.SetMicrophoneDevice(SelectedMicrophoneDevice);
        if (wasPreviewing)
            StartMicrophonePreview();
    }

    private void SyncSelectedMicrophoneItem()
    {
        var selectedItem = _microphonePriorityList
            .Select(priorityItem => Microphones.FirstOrDefault(m =>
                string.Equals(m.Id, priorityItem.Id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(item => item is not null);

        selectedItem ??= Microphones.FirstOrDefault(m => m.DeviceNumber == SelectedMicrophoneDevice)
            ?? Microphones.FirstOrDefault(m => m.DeviceNumber is null);

        _isSyncingMicrophoneSelection = true;
        try
        {
            SelectedMicrophoneItem = selectedItem;
        }
        finally
        {
            _isSyncingMicrophoneSelection = false;
        }
    }

    private bool UpdateMicrophonePriorityFromSelectedItem(MicrophoneItem? item)
    {
        if (string.IsNullOrWhiteSpace(item?.Id))
            return SetMicrophonePriorityList([]);

        if (_microphonePriorityList.Count > 0)
            return false;

        return SetMicrophonePriorityList([new MicrophonePriorityItem(item.Id, item.Name)]);
    }

    private void MigrateLegacyMicrophoneSelection(IReadOnlyList<AudioInputDeviceInfo> availableDevices)
    {
        if (_microphonePriorityList.Count > 0 || SelectedMicrophoneDevice is not int selectedDevice)
            return;

        var device = availableDevices.FirstOrDefault(device => device.DeviceNumber == selectedDevice);
        if (device is null)
            return;

        _microphonePriorityList = NormalizeMicrophonePriorityList(
            [new MicrophonePriorityItem(device.Id, device.Name)]);
        SyncMicrophonePriorityItems();
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        _settings.Save(_settings.Current with { MicrophonePriorityList = _microphonePriorityList });
    }

    private bool SetMicrophonePriorityList(IReadOnlyList<MicrophonePriorityItem> priorityList)
    {
        var normalized = NormalizeMicrophonePriorityList(priorityList);
        if (MicrophonePriorityListsEqual(_microphonePriorityList, normalized))
            return false;

        _microphonePriorityList = normalized;
        SyncMicrophonePriorityItems();
        _audio.SetMicrophonePriorityList(_microphonePriorityList);
        SyncSelectedMicrophoneItem();
        NotifyMicrophonePriorityCommandStates();
        return true;
    }

    private void SyncMicrophonePriorityItems()
    {
        MicrophonePriorityItems.Clear();
        foreach (var item in _microphonePriorityList)
            MicrophonePriorityItems.Add(new MicrophonePriorityListItem(item.Id, item.Name));

        OnPropertyChanged(nameof(HasMicrophonePriorityItems));
    }

    private int IndexOfMicrophonePriorityItem(MicrophonePriorityListItem? item)
    {
        if (item is null)
            return -1;

        for (var i = 0; i < _microphonePriorityList.Count; i++)
        {
            if (string.Equals(_microphonePriorityList[i].Id, item.Id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private void NotifyMicrophonePriorityCommandStates()
    {
        AddMicrophonePriorityItemCommand.NotifyCanExecuteChanged();
        RemoveMicrophonePriorityItemCommand.NotifyCanExecuteChanged();
    }

    private static IReadOnlyList<MicrophonePriorityItem> NormalizeMicrophonePriorityList(
        IReadOnlyList<MicrophonePriorityItem> priorityList)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<MicrophonePriorityItem>();
        foreach (var item in priorityList)
        {
            var id = item.Id.Trim();
            if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                continue;

            var name = item.Name.Trim();
            normalized.Add(new MicrophonePriorityItem(
                id,
                string.IsNullOrWhiteSpace(name) ? id : name));
        }

        return normalized;
    }

    private static bool MicrophonePriorityListsEqual(
        IReadOnlyList<MicrophonePriorityItem> left,
        IReadOnlyList<MicrophonePriorityItem> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal));

    private static bool MicrophoneMatches(AudioInputDeviceInfo device, MicrophonePriorityItem priorityItem) =>
        string.Equals(device.Id, priorityItem.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(device.Name, priorityItem.Name, StringComparison.OrdinalIgnoreCase);

    private void RefreshSpokenFeedbackProviders()
    {
        ReplaceCollection(SpokenFeedbackProviders, _speechFeedback.AvailableProviders);

        if (SpokenFeedbackProviders.All(p =>
                !string.Equals(p.Id, SelectedSpokenFeedbackProviderId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedSpokenFeedbackProviderId = AppSettings.DefaultSpokenFeedbackProviderId;
        }

        RefreshSpokenFeedbackVoices();
    }

    private void RefreshSpokenFeedbackVoices()
    {
        var wasLoading = _isLoading;
        _isLoading = true;
        ReplaceCollection(SpokenFeedbackVoices, _speechFeedback.GetVoiceOptions(SelectedSpokenFeedbackProviderId));
        SelectedSpokenFeedbackVoiceId = _speechFeedback.GetSelectedVoiceId(SelectedSpokenFeedbackProviderId);
        _isLoading = wasLoading;
    }

    private bool IsBuiltInSpokenFeedbackProviderSelected() =>
        string.IsNullOrWhiteSpace(SelectedSpokenFeedbackProviderId)
        || string.Equals(
            SelectedSpokenFeedbackProviderId,
            AppSettings.DefaultSpokenFeedbackProviderId,
            StringComparison.OrdinalIgnoreCase);

    private HistoryRetentionOption MatchHistoryRetentionOption(HistoryRetentionMode mode, int minutes)
    {
        return HistoryRetentionOptions.FirstOrDefault(option =>
            option.Mode == mode &&
            (mode != HistoryRetentionMode.Duration || option.Minutes == minutes))
            ?? HistoryRetentionOptions.First(option =>
                option.Mode == AppSettings.Default.HistoryRetentionMode &&
                option.Minutes == AppSettings.Default.HistoryRetentionMinutes);
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }
}

/// <summary>
/// Represents microphone item data.
/// </summary>
/// <param name="DeviceNumber">Device number supplied to the member.</param>
/// <param name="Name">Name supplied to the member.</param>
/// <param name="Id">Stable device id supplied to the member.</param>
public sealed record MicrophoneItem(int? DeviceNumber, string Name, string? Id = null);
/// <summary>
/// Represents one microphone priority row.
/// </summary>
/// <param name="Id">Stable device id supplied to the member.</param>
/// <param name="Name">Display name supplied to the member.</param>
public sealed record MicrophonePriorityListItem(string Id, string Name);
/// <summary>
/// Represents a microphone priority drag reorder request.
/// </summary>
/// <param name="Source">Dragged priority item.</param>
/// <param name="Target">Priority item receiving the drop.</param>
public sealed record MicrophonePriorityReorderRequest(
    MicrophonePriorityListItem Source,
    MicrophonePriorityListItem Target);
/// <summary>
/// Represents overlay widget option data.
/// </summary>
/// <param name="Value">Value supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);
/// <summary>
/// Represents history retention option data.
/// </summary>
/// <param name="Mode">Mode supplied to the member.</param>
/// <param name="Minutes">Minutes supplied to the member.</param>
/// <param name="DisplayName">Display name supplied to the member.</param>
public sealed record HistoryRetentionOption(HistoryRetentionMode Mode, int? Minutes, string DisplayName);
/// <summary>
/// Represents command example data.
/// </summary>
/// <param name="Command">Command supplied to the member.</param>
public sealed record CommandExample(string Command);
