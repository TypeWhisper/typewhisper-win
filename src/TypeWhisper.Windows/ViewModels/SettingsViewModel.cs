using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

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
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private string? _translationTargetLanguage;
    [ObservableProperty] private bool _apiServerEnabled;
    [ObservableProperty] private int _apiServerPort = 9876;

    public IReadOnlyList<TranslationTargetOption> TranslationTargetOptions { get; } = TranslationModelInfo.GlobalTargetOptions;
    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];

    private bool _isLoading;

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
        Microphones.Add(new MicrophoneItem(null, "Standard"));
        foreach (var (number, name) in AudioRecordingService.GetAvailableDevices())
        {
            Microphones.Add(new MicrophoneItem(number, name));
        }
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
            PauseMediaDuringRecording = PauseMediaDuringRecording
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
    }
}

public sealed record MicrophoneItem(int? DeviceNumber, string Name);
