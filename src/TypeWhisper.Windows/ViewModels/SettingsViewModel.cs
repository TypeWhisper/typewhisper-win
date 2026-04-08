using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;

    [ObservableProperty] private string _toggleHotkey = "";
    [ObservableProperty] private string _pushToTalkHotkey = "";
    [ObservableProperty] private string _toggleOnlyHotkey = "";
    [ObservableProperty] private string _holdOnlyHotkey = "";
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _autoPaste = true;
    [ObservableProperty] private RecordingMode _mode = RecordingMode.Toggle;
    [ObservableProperty] private bool _whisperModeEnabled;
    [ObservableProperty] private bool _soundFeedbackEnabled = true;
    [ObservableProperty] private bool _audioDuckingEnabled;
    [ObservableProperty] private float _audioDuckingLevel = 0.2f;
    [ObservableProperty] private bool _pauseMediaDuringRecording;
    [ObservableProperty] private bool _silenceAutoStopEnabled;
    [ObservableProperty] private int _silenceAutoStopSeconds = 10;
    [ObservableProperty] private OverlayPosition _overlayPosition = OverlayPosition.Bottom;
    [ObservableProperty] private int _historyRetentionDays = 90;
    [ObservableProperty] private string _transcriptionTask = "transcribe";
    [ObservableProperty] private int? _selectedMicrophoneDevice;
    [ObservableProperty] private float _previewLevel;
    [ObservableProperty] private bool _saveToHistoryEnabled = true;
    [ObservableProperty] private bool _spokenFeedbackEnabled;
    [ObservableProperty] private bool _memoryEnabled;
    [ObservableProperty] private int _autoUnloadMinutes;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private bool _apiServerEnabled;
    [ObservableProperty] private int _apiServerPort = 9876;
    [ObservableProperty] private OverlayWidget _overlayLeftWidget = OverlayWidget.Waveform;
    [ObservableProperty] private OverlayWidget _overlayRightWidget = OverlayWidget.Timer;
    [ObservableProperty] private string _promptPaletteHotkey = "";
    [ObservableProperty] private string? _uiLanguage;

    public IReadOnlyList<TranslationTargetOption> TranslationTargetOptions { get; } = LocalizeTranslationOptions(TranslationModelInfo.GlobalTargetOptions);

    private static IReadOnlyList<TranslationTargetOption> LocalizeTranslationOptions(IReadOnlyList<TranslationTargetOption> options) =>
        options.Select(o => o.DisplayName switch
        {
            "Keine Übersetzung" => o with { DisplayName = Loc.Instance["Translation.None"] },
            "Globale Einstellung" => o with { DisplayName = Loc.Instance["Translation.GlobalSetting"] },
            _ => o
        }).ToList();
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];

    public static IReadOnlyList<OverlayWidgetOption> WidgetOptions { get; } =
    [
        new(OverlayWidget.None, Loc.Instance["Widget.None"]),
        new(OverlayWidget.Indicator, Loc.Instance["Widget.Indicator"]),
        new(OverlayWidget.Timer, Loc.Instance["Widget.Timer"]),
        new(OverlayWidget.Waveform, Loc.Instance["Widget.Waveform"]),
        new(OverlayWidget.Clock, Loc.Instance["Widget.Clock"]),
        new(OverlayWidget.Profile, Loc.Instance["Widget.Profile"]),
        new(OverlayWidget.HotkeyMode, Loc.Instance["Widget.HotkeyMode"]),
        new(OverlayWidget.AppName, Loc.Instance["Widget.AppName"]),
    ];

    private bool _isLoading;

    partial void OnUiLanguageChanged(string? value)
    {
        if (_isLoading) return;
        Loc.Instance.CurrentLanguage = value ?? Loc.Instance.DetectSystemLanguage();
    }

    partial void OnSelectedMicrophoneDeviceChanged(int? value)
    {
        if (_isLoading) return;
        StartMicrophonePreview();
    }

    public SettingsViewModel(ISettingsService settings, AudioRecordingService audio)
    {
        _settings = settings;
        _audio = audio;

        _isLoading = true;
        LoadFromSettings(_settings.Current);
        AutostartEnabled = StartupService.IsEnabled;
        RefreshMicrophones();
        _isLoading = false;

        PropertyChanged += (_, _) =>
        {
            if (!_isLoading) Save();
        };
    }

    [RelayCommand]
    private void RefreshMicrophones()
    {
        Microphones.Clear();
        Microphones.Add(new MicrophoneItem(null, Loc.Instance["Microphone.Default"]));
        foreach (var (number, name) in AudioRecordingService.GetAvailableDevices())
        {
            Microphones.Add(new MicrophoneItem(number, name));
        }
    }

    public void StartMicrophonePreview()
    {
        _audio.PreviewLevelChanged -= OnPreviewLevelChanged;
        if (!_audio.HasDevice) return;
        _audio.StartPreview(SelectedMicrophoneDevice);
        _audio.PreviewLevelChanged += OnPreviewLevelChanged;
    }

    public void StopMicrophonePreview()
    {
        _audio.PreviewLevelChanged -= OnPreviewLevelChanged;
        _audio.StopPreview();
        PreviewLevel = 0;
    }

    private void OnPreviewLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            PreviewLevel = Math.Min(e.RmsLevel * 5f, 1f)); // Scale for visibility
    }

    [RelayCommand]
    private void Save()
    {
        var updated = _settings.Current with
        {
            ToggleHotkey = ToggleHotkey,
            PushToTalkHotkey = PushToTalkHotkey,
            Language = Language,
            AutoPaste = AutoPaste,
            Mode = Mode,
            WhisperModeEnabled = WhisperModeEnabled,
            SoundFeedbackEnabled = SoundFeedbackEnabled,
            SilenceAutoStopEnabled = SilenceAutoStopEnabled,
            SilenceAutoStopSeconds = SilenceAutoStopSeconds,
            OverlayPosition = OverlayPosition,
            HistoryRetentionDays = HistoryRetentionDays,
            TranscriptionTask = TranscriptionTask,
            SelectedMicrophoneDevice = SelectedMicrophoneDevice,
            TranslationTargetLanguage = TranslationTargetLanguage,
            ApiServerEnabled = ApiServerEnabled,
            ApiServerPort = ApiServerPort,
            ToggleOnlyHotkey = ToggleOnlyHotkey,
            HoldOnlyHotkey = HoldOnlyHotkey,
            AudioDuckingEnabled = AudioDuckingEnabled,
            AudioDuckingLevel = AudioDuckingLevel,
            PauseMediaDuringRecording = PauseMediaDuringRecording,
            OverlayLeftWidget = OverlayLeftWidget,
            OverlayRightWidget = OverlayRightWidget,
            PromptPaletteHotkey = PromptPaletteHotkey,
            SaveToHistoryEnabled = SaveToHistoryEnabled,
            SpokenFeedbackEnabled = SpokenFeedbackEnabled,
            MemoryEnabled = MemoryEnabled,
            ModelAutoUnloadSeconds = AutoUnloadMinutes * 60,
            UiLanguage = UiLanguage
        };
        _settings.Save(updated);
        StartupService.SetEnabled(AutostartEnabled);
    }

    private void LoadFromSettings(AppSettings s)
    {
        ToggleHotkey = s.ToggleHotkey;
        PushToTalkHotkey = s.PushToTalkHotkey;
        Language = s.Language;
        AutoPaste = s.AutoPaste;
        Mode = s.Mode;
        WhisperModeEnabled = s.WhisperModeEnabled;
        SoundFeedbackEnabled = s.SoundFeedbackEnabled;
        SilenceAutoStopEnabled = s.SilenceAutoStopEnabled;
        SilenceAutoStopSeconds = s.SilenceAutoStopSeconds;
        OverlayPosition = s.OverlayPosition;
        HistoryRetentionDays = s.HistoryRetentionDays;
        TranscriptionTask = s.TranscriptionTask;
        SelectedMicrophoneDevice = s.SelectedMicrophoneDevice;
        TranslationTargetLanguage = s.TranslationTargetLanguage;
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        ToggleOnlyHotkey = s.ToggleOnlyHotkey;
        HoldOnlyHotkey = s.HoldOnlyHotkey;
        AudioDuckingEnabled = s.AudioDuckingEnabled;
        AudioDuckingLevel = s.AudioDuckingLevel;
        PauseMediaDuringRecording = s.PauseMediaDuringRecording;
        OverlayLeftWidget = s.OverlayLeftWidget;
        OverlayRightWidget = s.OverlayRightWidget;
        PromptPaletteHotkey = s.PromptPaletteHotkey;
        SaveToHistoryEnabled = s.SaveToHistoryEnabled;
        SpokenFeedbackEnabled = s.SpokenFeedbackEnabled;
        MemoryEnabled = s.MemoryEnabled;
        AutoUnloadMinutes = s.ModelAutoUnloadSeconds / 60;
        UiLanguage = s.UiLanguage;
    }
}

public sealed record MicrophoneItem(int? DeviceNumber, string Name);
public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);
