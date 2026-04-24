using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly AudioRecordingService _audio;
    private readonly ApiServerController _apiServer;
    private readonly CliInstallService _cliInstall;

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
    [ObservableProperty] private HistoryRetentionOption? _selectedHistoryRetentionOption;
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
    [ObservableProperty] private int _apiServerPort = 8978;
    [ObservableProperty] private OverlayWidget _overlayLeftWidget = OverlayWidget.Waveform;
    [ObservableProperty] private OverlayWidget _overlayRightWidget = OverlayWidget.Timer;
    [ObservableProperty] private string _promptPaletteHotkey = "";
    [ObservableProperty] private string? _uiLanguage;
    [ObservableProperty] private string _apiServerStatusText = "";
    [ObservableProperty] private string _apiServerErrorText = "";
    [ObservableProperty] private bool _apiServerHasError;
    [ObservableProperty] private string _cliStatusText = "";
    [ObservableProperty] private string _cliBundledPathText = "";
    [ObservableProperty] private bool _cliBundledAvailable;
    [ObservableProperty] private bool _cliInstalled;

    public ObservableCollection<TranslationTargetOption> TranslationTargetOptions { get; } = [];
    public ObservableCollection<HistoryRetentionOption> HistoryRetentionOptions { get; } = [];
    public ObservableCollection<CommandExample> CurlExamples { get; } = [];
    public ObservableCollection<CommandExample> CliExamples { get; } = [];

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
        new(OverlayWidget.Profile, Loc.Instance["Widget.Profile"]),
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

    public ObservableCollection<MicrophoneItem> Microphones { get; } = [];
    public ObservableCollection<OverlayWidgetOption> WidgetOptions { get; } = [];

    private bool _isLoading;

    partial void OnUiLanguageChanged(string? value)
    {
        if (_isLoading) return;
        Loc.Instance.CurrentLanguage = value ?? Loc.Instance.DetectSystemLanguage();
    }

    partial void OnSelectedMicrophoneDeviceChanged(int? value)
    {
        if (_isLoading) return;
        _audio.SetMicrophoneDevice(value);
        StopMicrophonePreview();
    }

    public SettingsViewModel(
        ISettingsService settings,
        AudioRecordingService audio,
        ApiServerController apiServer,
        CliInstallService cliInstall)
    {
        _settings = settings;
        _audio = audio;
        _apiServer = apiServer;
        _cliInstall = cliInstall;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        _apiServer.StateChanged += OnApiServerStateChanged;

        _isLoading = true;
        RefreshLocalizedCollections();
        LoadFromSettings(_settings.Current);
        AutostartEnabled = StartupService.IsEnabled;
        RefreshMicrophones();
        _audio.SetMicrophoneDevice(SelectedMicrophoneDevice);
        RefreshApiServerStatus();
        RefreshCliState();
        RefreshApiExamples();
        _isLoading = false;

        _settings.SettingsChanged += OnSettingsChanged;

        PropertyChanged += (_, args) =>
        {
            if (_isLoading) return;

            if (args.PropertyName == nameof(AutostartEnabled))
            {
                ApplyAutostartSetting();
                return;
            }

            Save();
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

    public void StartMicrophonePreview()
    {
        _audio.PreviewLevelChanged -= OnPreviewLevelChanged;
        if (!_audio.HasDevice) return;
        _audio.SetMicrophoneDevice(SelectedMicrophoneDevice);
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
        var mainDictationHotkey = HotkeyParser.Normalize(PushToTalkHotkey);

        var updated = _settings.Current with
        {
            ToggleHotkey = mainDictationHotkey,
            PushToTalkHotkey = mainDictationHotkey,
            Language = Language,
            AutoPaste = AutoPaste,
            Mode = Mode,
            WhisperModeEnabled = WhisperModeEnabled,
            SoundFeedbackEnabled = SoundFeedbackEnabled,
            SilenceAutoStopEnabled = SilenceAutoStopEnabled,
            SilenceAutoStopSeconds = SilenceAutoStopSeconds,
            OverlayPosition = OverlayPosition,
            HistoryRetentionMode = SelectedHistoryRetentionOption?.Mode ?? AppSettings.Default.HistoryRetentionMode,
            HistoryRetentionMinutes = SelectedHistoryRetentionOption?.Minutes ?? AppSettings.Default.HistoryRetentionMinutes,
            TranscriptionTask = TranscriptionTask,
            SelectedMicrophoneDevice = SelectedMicrophoneDevice,
            TranslationTargetLanguage = TranslationTargetLanguage,
            ApiServerEnabled = ApiServerEnabled,
            ApiServerPort = ApiServerPort,
            ToggleOnlyHotkey = HotkeyParser.Normalize(ToggleOnlyHotkey),
            HoldOnlyHotkey = HotkeyParser.Normalize(HoldOnlyHotkey),
            AudioDuckingEnabled = AudioDuckingEnabled,
            AudioDuckingLevel = AudioDuckingLevel,
            PauseMediaDuringRecording = PauseMediaDuringRecording,
            OverlayLeftWidget = OverlayLeftWidget,
            OverlayRightWidget = OverlayRightWidget,
            PromptPaletteHotkey = HotkeyParser.Normalize(PromptPaletteHotkey),
            SaveToHistoryEnabled = SaveToHistoryEnabled,
            SpokenFeedbackEnabled = SpokenFeedbackEnabled,
            MemoryEnabled = MemoryEnabled,
            ModelAutoUnloadSeconds = AutoUnloadMinutes * 60,
            UiLanguage = UiLanguage
        };
        _settings.Save(updated);
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
        var pushToTalkHotkey = HotkeyParser.Normalize(s.PushToTalkHotkey);
        var toggleHotkey = HotkeyParser.Normalize(s.ToggleHotkey);
        var mainDictationHotkey = !string.IsNullOrWhiteSpace(pushToTalkHotkey)
            ? pushToTalkHotkey
            : toggleHotkey;

        ToggleHotkey = string.IsNullOrWhiteSpace(toggleHotkey)
            ? mainDictationHotkey
            : toggleHotkey;
        PushToTalkHotkey = mainDictationHotkey;
        Language = s.Language;
        AutoPaste = s.AutoPaste;
        Mode = s.Mode;
        WhisperModeEnabled = s.WhisperModeEnabled;
        SoundFeedbackEnabled = s.SoundFeedbackEnabled;
        SilenceAutoStopEnabled = s.SilenceAutoStopEnabled;
        SilenceAutoStopSeconds = s.SilenceAutoStopSeconds;
        OverlayPosition = s.OverlayPosition;
        SelectedHistoryRetentionOption = MatchHistoryRetentionOption(s.HistoryRetentionMode, s.HistoryRetentionMinutes);
        TranscriptionTask = s.TranscriptionTask;
        SelectedMicrophoneDevice = s.SelectedMicrophoneDevice;
        TranslationTargetLanguage = s.TranslationTargetLanguage;
        ApiServerEnabled = s.ApiServerEnabled;
        ApiServerPort = s.ApiServerPort;
        ToggleOnlyHotkey = HotkeyParser.Normalize(s.ToggleOnlyHotkey);
        HoldOnlyHotkey = HotkeyParser.Normalize(s.HoldOnlyHotkey);
        AudioDuckingEnabled = s.AudioDuckingEnabled;
        AudioDuckingLevel = s.AudioDuckingLevel;
        PauseMediaDuringRecording = s.PauseMediaDuringRecording;
        OverlayLeftWidget = s.OverlayLeftWidget;
        OverlayRightWidget = s.OverlayRightWidget;
        PromptPaletteHotkey = HotkeyParser.Normalize(s.PromptPaletteHotkey);
        SaveToHistoryEnabled = s.SaveToHistoryEnabled;
        SpokenFeedbackEnabled = s.SpokenFeedbackEnabled;
        MemoryEnabled = s.MemoryEnabled;
        AutoUnloadMinutes = s.ModelAutoUnloadSeconds / 60;
        UiLanguage = s.UiLanguage;
        RefreshApiExamples();
        RefreshApiServerStatus();
    }

    private void OnSettingsChanged(AppSettings updatedSettings)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _isLoading = true;
            LoadFromSettings(updatedSettings);
            AutostartEnabled = StartupService.IsEnabled;
            RefreshApiServerStatus();
            _isLoading = false;
        });
    }

    private void OnApiServerStateChanged()
    {
        Application.Current?.Dispatcher.InvokeAsync(RefreshApiServerStatus);
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
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _isLoading = true;
            RefreshLocalizedCollections();
            SelectedHistoryRetentionOption = MatchHistoryRetentionOption(
                _settings.Current.HistoryRetentionMode,
                _settings.Current.HistoryRetentionMinutes);
            _isLoading = false;
        });
    }

    private void RefreshLocalizedCollections()
    {
        ReplaceCollection(TranslationTargetOptions, LocalizeTranslationOptions(TranslationModelInfo.GlobalTargetOptions));
        ReplaceCollection(HistoryRetentionOptions, BuildHistoryRetentionOptions());
        ReplaceCollection(WidgetOptions, BuildWidgetOptions());
        RefreshMicrophones();
    }

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

public sealed record MicrophoneItem(int? DeviceNumber, string Name);
public sealed record OverlayWidgetOption(OverlayWidget Value, string DisplayName);
public sealed record HistoryRetentionOption(HistoryRetentionMode Mode, int? Minutes, string DisplayName);
public sealed record CommandExample(string Command);
