using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly WinUIHttpApi _httpApi;
    private const int CompactWidth = 780;
    private const int CompactHeight = 520;
    private static string LauncherHotkeyPath => WinUIProfile.DataPath("quick-launch-hotkeys.txt");

    private string? ChangeLauncherHotkeys(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_hotkeyRegistration is null) return "Global hotkey service is unavailable. Restart the app.";
        if (_cancelProcessingHotkey?.ConflictWithLauncher(value) is { } conflict) return conflict;
        if (_workflowShortcuts?.Conflict(value) is { } workflowConflict) return workflowConflict;
        if (HistoryShortcutConflict(value) is { } historyConflict) return historyConflict;
        if (CopyLastShortcutConflict(value) is { } copyConflict) return copyConflict;
        if (ReadLastShortcutConflict(value) is { } readConflict) return readConflict;
        var previous = _hotkeyRegistration.Value;
        var error = _hotkeyRegistration.TryChange(value);
        if (error is not null) return error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherHotkeyPath)!);
            File.WriteAllText(LauncherHotkeyPath, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollbackError = _hotkeyRegistration.TryChange(previous);
            _settingsValues["QuickLaunchHotkeys"] = _hotkeyRegistration.Value;
            HotkeyHint.Text = _hotkeyRegistration.DisplayText;
            return rollbackError ?? $"Could not save the shortcut: {ex.Message}";
        }
        HotkeyHint.Text = _hotkeyRegistration.DisplayText;
        return null;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    private static readonly IReadOnlyList<Command> Commands =
    [
        new("Pinned", "microphone", "Dictation", "Focus a text field, then use your dictation shortcut", "", "Configure Main dictation in Settings > Shortcuts. Use the shortcut to start, and again to finish."),
        new("Pinned", "history", "History", "Browse, search, copy, and export transcriptions", "H", "Opens History in workspace mode. Full transcript search remains inside this explicit scope."),
        new("Pinned", "recorder", "Recorder", "Record microphone and system audio", "R", "Opens the recorder workspace without interrupting active dictation."),
        new("Pinned", "workflow", "Workflows", "Run and manage reusable text workflows", "W", "Choose a workflow, inspect its provider, and run it against selected or dictated text."),
        new("Suggested", "plugin", "Integrations", "Discover plugins and manage provider settings", "", "Shows installed plugins, their health, permissions, settings, and updates."),
        new("Suggested", "settings", "Settings", "Audio, hotkeys, privacy, account, and updates", "Ctrl ,", "Opens the dedicated Settings surface for global application configuration."),
        new("Suggested", "file", "Transcribe file", "Drop or choose audio and video files", "", "Opens the file transcription queue in workspace mode."),
        new("Suggested", "dictionary", "Dictionary", "Your words and preferred spellings", "D", "Manage words and correction rules used by TypeWhisper."),
        new("Suggested", "text", "Snippets", "Reusable text with spoken triggers", "", "Create and edit text snippets."),
        new("Suggested", "file", "Copy last transcription", "Copy the last completed dictation from this session", "", "Copies final dictated text, including when History is off. Configure its global shortcut in Settings > Shortcuts."),
        new("Suggested", "audio", "Read last transcription", "Read the last dictation aloud; run again to stop", "", "Uses the selected Windows voice and audio output. Works independently of automatic spoken feedback."),
        new("Suggested", "home", "Dashboard", "Your activity and recent transcriptions", "", "Opens the activity dashboard."),
        new("Suggested", "stats", "Statistics", "Words, streaks, apps, and models", "", "Explore your usage over time."),
    ];

    internal ObservableCollection<Command> FilteredItems { get; } = [];

    private readonly Stopwatch _activationStopwatch = Stopwatch.StartNew();
    private readonly HotkeyRegistration? _hotkeyRegistration;
    private DictationHotkeyRegistration? _dictationHotkey;
    private ProcessingCancelHotkeyRegistration? _cancelProcessingHotkey;
    private TypeWhisper.Presentation.DictationInputCoordinator? _dictationInput;
    private Action? _observeInputMode;
    private static string DictationHotkeyPath => Path.Combine(Path.GetDirectoryName(LauncherHotkeyPath)!, "dictation-hotkeys.txt");
    private string? ChangeDictationHotkeys(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_dictationHotkey is null) return "Dictation hotkeys are unavailable. Restart the app.";
        if (_cancelProcessingHotkey?.ConflictWithDictation(value) is { } conflict) return conflict;
        if (_workflowShortcuts?.Conflict(value, modifierOnly: true) is { } workflowConflict) return workflowConflict;
        if (HistoryShortcutConflict(value, modifierOnly: true) is { } historyConflict) return historyConflict;
        if (CopyLastShortcutConflict(value, modifierOnly: true) is { } copyConflict) return copyConflict;
        if (ReadLastShortcutConflict(value, modifierOnly: true) is { } readConflict) return readConflict;
        if (_dictation.IsRecording) return "Finish the recording before changing its shortcut.";
        var previous = _dictationHotkey.Value;
        var error = _dictationHotkey.TryChange(value);
        if (error is not null) return error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DictationHotkeyPath)!);
            File.WriteAllText(DictationHotkeyPath + ".tmp", _dictationHotkey.Value);
            File.Move(DictationHotkeyPath + ".tmp", DictationHotkeyPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rollback = _dictationHotkey.TryChange(previous);
            _settingsValues["MainDictationHotkeys"] = _dictationHotkey.Value;
            _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
            return rollback ?? $"Could not save the shortcut: {ex.Message}";
        }
        _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
        return null;
    }
    private readonly LocalDictationSession _dictation;
    private OverlayWindow? _liveOverlay;
    internal event Action<string, bool>? DictationChanged;

    private Task? _dictationInitialization;
    internal Task InitializeDictationAsync() => _dictationInitialization ??= InitializeDictationCoreAsync();
    private async Task InitializeDictationCoreAsync()
    {
        if (_closing) return;
        try
        {
            _dictationInput = new(
                () => { _dictation.LivePreviewEnabled = _transcriptPreviewEnabled; return _dictation.StartAsync(); },
                _dictation.StopAsync, _dictation.CancelAsync,
                () => _dictation.IsRecording, () => !DictationHotkeysPaused && _dictation.CanChangeProvider && _dictation.CanStartFromShortcut,
                () => _dictation.RecordingModePreferences.Current,
                dispatch: action => DispatcherQueue.TryEnqueue(() => action()),
                reportError: error => System.Diagnostics.Debug.WriteLine("Dictation input failed: " + error.GetType().Name));
            _observeInputMode = () =>
            {
                if (DispatcherQueue.HasThreadAccess) _dictationInput.ObserveMode();
                else DispatcherQueue.TryEnqueue(() => _dictationInput.ObserveMode());
            };
            _dictation.Changed += _observeInputMode;
            _dictationHotkey = new DictationHotkeyRegistration(this, action =>
            {
                if (action == HybridHotkeyAction.Cancel) _dictation.RequestCancel();
                _ = _dictationInput.SubmitAsync(action switch
                {
                    HybridHotkeyAction.Start => TypeWhisper.Presentation.DictationInputAction.Start,
                    HybridHotkeyAction.Stop => TypeWhisper.Presentation.DictationInputAction.Stop,
                    HybridHotkeyAction.Cancel => TypeWhisper.Presentation.DictationInputAction.Cancel,
                    _ => TypeWhisper.Presentation.DictationInputAction.Toggle
                });
            }, () => _dictationInput.IsRecordingOrStarting, () => _dictation.RecordingModePreferences.Current,
                () => DictationHotkeysPaused);
            var saved = File.Exists(DictationHotkeyPath) ? File.ReadAllText(DictationHotkeyPath) : "Ctrl+Shift+F9";
            var error = _dictationHotkey.TryChange(saved);
            _settingsValues["MainDictationHotkeys"] = _dictationHotkey.Value;
            _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
            if (error is not null) { MetricsText.Text = error; DictationChanged?.Invoke(error, false); return; }
            string? cancelError;
            try
            {
                _cancelProcessingHotkey = new(this, () => CanCancelProcessing, RequestProcessingCancellation,
                    () => _hotkeyRegistration?.Value ?? "", () => _dictationHotkey?.Value ?? "");
                cancelError = _cancelProcessingHotkey.Initialize();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Trace.TraceError("Cancel shortcut registration failed: {0}", ex);
                cancelError = "Cancel shortcuts are unavailable. Dictation can still be used; assign cancellation again in Settings.";
            }
            _settingsValues["CancelProcessingHotkeys"] = _cancelProcessingHotkey?.Value ?? "";
            await _dictation.InitializeAsync();
            if (!_closing) await _httpApi.InitializeAsync();
            EnsureFileTranscription();
            InitializeWorkflowShortcuts();
            InitializeHistoryShortcut();
            InitializeCopyLastShortcut();
            InitializeReadLastShortcut();
            if (cancelError is not null && !_closing) MetricsText.Text = cancelError;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closing) MetricsText.Text = "Dictation startup failed: " + ex.Message; }
    }

    internal void FinishDictationFromTray() { if (_dictation.IsRecording) _ = _dictation.ToggleAsync(); }
    internal bool CanCancelProcessing => !_closing && (_dictation.CanCancelProcessing || _workflowCancellation is not null);
    internal async Task CancelProcessingAsync()
    {
        if (!CanCancelProcessing) return;
        RequestWorkflowCancellation();
        try { if (_dictation.CanCancelProcessing) await _dictation.CancelAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Processing cancellation failed: {0}", ex); if (!_closing) MetricsText.Text = "Could not finish cancellation. Try again."; }
    }
    internal Func<Task<string?>>? RestartApplicationAsync { get; set; }
    private Task<string?> RestartForPluginUpdateAsync()
    {
        if (_closing || _profileRestoreClosing || _dictation.Packages.Updates.Busy || !_dictation.CanChangeProvider || _dictation.Models.Busy || _dictation.CtcVocabulary.Busy)
            return Task.FromResult<string?>("Finish recording and processing before restarting TypeWhisper.");
        return RestartApplicationAsync?.Invoke() ?? Task.FromResult<string?>("Restart is currently unavailable.");
    }
    private bool _closing;
    private readonly TypeWhisper.Presentation.AsyncShutdownCoordinator _shutdown = new();
    internal async Task ShutdownDictationAsync()
    {
        _cancelProcessingHotkey?.Dispose();
        _historyHotkey?.Dispose();
        _copyLastHotkey?.Dispose();
        _readLastHotkey?.Dispose();
        await StopWorkflowShortcutsAsync();
        // The recorder owns the session gate while capturing; save it before session shutdown waits for that gate.
        var reviews = DrainReviewWindowsAsync();
        await Task.WhenAll(RecorderView.ShutdownAsync(), reviews);
        await ShutdownCoreAsync();
    }
    private Task ShutdownCoreAsync() => _shutdown.Run(async () =>
    {
        _closing = true;
        await StopWorkflowShortcutsAsync();
        _cancelProcessingHotkey?.Dispose();
        _historyHotkey?.Dispose();
        _copyLastHotkey?.Dispose();
        _readLastHotkey?.Dispose();
        _dictationHotkey?.Dispose();
        _dictationInput?.Dispose();
        if (_observeInputMode is not null) _dictation.Changed -= _observeInputMode;
        MetricsText.Text = "Finishing shutdown…";
        var reviews = DrainReviewWindowsAsync();
        // Finish settings confirmations, preference writes and recovery retries before
        // session shutdown disposes the recovery store they use.
        await DrainRecoveryViewsAsync();
        // Begin all cancellation requests before awaiting any drain.
        var api = _httpApi.ShutdownAsync();
        var session = _dictation.ShutdownAsync();
        var files = _fileTranscription?.ShutdownAsync() ?? Task.CompletedTask;
        var history = HistoryView.ShutdownAsync();
        var workflows = WorkflowsView.ShutdownAsync();
        var lexicon = _lexicon?.ShutdownAsync() ?? Task.CompletedTask;
        await Task.WhenAll(api, session, files, history, workflows, lexicon, reviews, _profileUiDrain ?? Task.CompletedTask, _dictationInput?.Completion ?? Task.CompletedTask,
            _dictationInitialization ?? Task.CompletedTask);
        _liveOverlay?.Close();
    });
    internal void ShowShutdownFailure()
    {
        ShowFromActivation();
        if (RecorderView.NeedsSaveRetry)
        {
            if (!_recorderOpen) OpenRecorder();
            MetricsText.Text = "Recording could not be saved. Retry saving in Recorder, then choose Exit again.";
            return;
        }
        MetricsText.Text = "Shutdown could not complete cleanly. Work is stopped; see the diagnostic log for details.";
    }
    internal bool CanRetryRecorderShutdown => RecorderView.NeedsSaveRetry;

    private void ShowLearnedCorrections(IReadOnlyList<TypeWhisper.Core.Models.LearnedDictionaryCorrection> corrections)
    {
        if (_closing || _dictation.IsRecording || _dictation.OverlayState.Phase == DictationPhase.Processing) return;
        ++_overlayRevision;
        _completedRecordingId = Guid.Empty;
        _completedPreviewExpired = true;
        _overlay?.HidePreview();
        _liveOverlay ??= new OverlayWindow(false, () => _dictation.IsRecording ? _dictation.CurrentLevel : 0,
            () => _dictation.OverlayState, () => _dictation.LivePreviewText);
        _liveOverlay.SetLayout(OverlayPreferences);
        _liveOverlay.ShowCorrectionFeedback(corrections, DisplayArea.GetFromWindowId(_liveOverlay.AppWindow.Id, DisplayAreaFallback.Primary));
    }
    private void HideLearnedCorrections()
    {
        if (_liveOverlay?.IsCorrectionFeedbackVisible == true) _liveOverlay.HidePreview();
    }

    private void UpdateLiveDictation()
    {
        if (_closing) return;
        var revision = ++_overlayRevision;
        if (IsNormalLauncherStatus) MetricsText.Text = DictationStatusForDisplay;
        UpdateTranscriptToggle();
        DictationChanged?.Invoke(_dictation.Status, _dictation.IsRecording);
        if (_liveOverlay?.IsCorrectionFeedbackVisible == true &&
            _dictation.OverlayState.Phase is not (DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error)) return;
        if (_dictation.OverlayState.Phase != DictationPhase.Completed) _completedPreviewExpired = false;
        else if (_completedPreviewExpired) return;
        else if (OverlayPreferences.PreviewBubbleAutoHideMilliseconds == 0)
        {
            _completedPreviewExpired = true;
            _liveOverlay?.HidePreview();
            return;
        }
        if (_dictation.OverlayState.Phase is DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error or DictationPhase.Completed)
        {
            _overlay?.HidePreview();
            var showTranscript = _dictation.OverlayState.ShouldShowTranscript(_transcriptPreviewEnabled, _dictation.SupportsLiveTranscription);
            if (_liveOverlay is null)
                _liveOverlay = new OverlayWindow(showTranscript, () => _dictation.IsRecording ? _dictation.CurrentLevel : 0, () => _dictation.OverlayState, () => _dictation.LivePreviewText);
            // Apply before showing a reused overlay, so a cloud recording cannot flash its old text window.
            _liveOverlay.SetTranscriptPreviewEnabled(showTranscript);
            _liveOverlay.SetLayout(OverlayPreferences);
            _liveOverlay.SetMode(_overlayMode, DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
            _liveOverlay.ActivateWithoutTakingFocus();
            _liveOverlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
            if (_dictation.OverlayState.Phase == DictationPhase.Error) _ = HideErrorOverlayAsync(revision);
        }
        else
        {
            _liveOverlay?.HidePreview();
            if (_historyOpen) _ = HistoryView.RefreshAsync();
        }
    }
    private int _overlayRevision;
    private Guid _completedRecordingId;
    private bool _completedPreviewExpired;
    private async Task HideCompletedOverlayAsync(Guid recordingId)
    {
        _completedRecordingId = recordingId;
        var delay = OverlayPreferences.PreviewBubbleAutoHideMilliseconds;
        if (delay > 0) await Task.Delay(delay);
        if (_completedRecordingId == recordingId && _dictation.OverlayState.Phase == DictationPhase.Completed)
        {
            _completedPreviewExpired = true;
            _liveOverlay?.HidePreview();
        }
    }
    private async Task HideErrorOverlayAsync(int revision)
    {
        await Task.Delay(5000);
        if (_overlayRevision == revision) _liveOverlay?.HidePreview();
    }
    private Command? _selected;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private bool _technicalDetailsEnabled;
    private bool _isSearchEditing;
    private bool _transcriptPreviewEnabled = true;
    private uint _appliedWindowDpi;
    private OverlayMode _overlayMode = OverlayMode.Standard;
    private bool _historyOpen;
    private bool _recorderOpen;
    private bool _workflowsOpen;
    private bool _pluginsOpen;
    private bool _marketplaceOpen;
    private string _launcherQuery = string.Empty;
    private FileTranscriptionView? _fileTranscription;
    private bool FileTranscriptionOpen => FileTranscriptionHost.Visibility == Visibility.Visible;
    private LexiconView? _lexicon;
    private bool LexiconOpen => LexiconHost.Visibility == Visibility.Visible;

    internal void ShowOutputReview(TypeWhisper.Presentation.DictationOutputResult result)
    {
        if (_closing || _profileRestoreClosing || _reviewAdmissionClosed) return;
        var review = new DictationReviewWindow(result, _dictation.PluginRuntime);
        _reviewWindows.Add(review);
        review.Closed += (_, _) => _reviewWindows.Remove(review);
        review.Activate();
    }

    private readonly List<DictationReviewWindow> _reviewWindows = [];
    private bool _reviewAdmissionClosed;
    private Task DrainReviewWindowsAsync()
    {
        _reviewAdmissionClosed = true;
        return Task.WhenAll(_reviewWindows.ToArray().Select(review => review.ShutdownAsync()));
    }

    internal MainWindow()
    {
        InitializeComponent();
        CorrectionLearning.CorrectionsLearned += ShowLearnedCorrections;
        CorrectionLearning.ObservationCancelled += HideLearnedCorrections;
        Closed += (_, _) =>
        {
            CorrectionLearning.CorrectionsLearned -= ShowLearnedCorrections;
            CorrectionLearning.ObservationCancelled -= HideLearnedCorrections;
        };
#if DEBUG
        ConfigureTrayProbe();
#endif
        NativeWindowAppearance.ApplyAppTitleBar(this);
        LoadOverlayPreferences();
        var historyPath = WinUIProfile.DataPath("history.json");
#if DEBUG
        // Opt-in fixture uses an ephemeral history store, never the development profile's history.
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
            historyPath = Path.Combine(Path.GetTempPath(), "TypeWhisper-WinUI-HistoryFixture", Guid.NewGuid().ToString("N"), "history.json");
#endif
        var historyAudio = new TypeWhisper.Core.Services.HistoryAudioStore(Path.Combine(Path.GetDirectoryName(historyPath)!, "history-audio"));
        var historyService = new TypeWhisper.Core.Services.HistoryService(historyPath, audioStore: historyAudio) { ThrowOnLoadFailure = true };
#if DEBUG
        if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_FIXTURE") == "1")
        {
            var fixtureRecord = new TypeWhisper.Core.Models.TranscriptionRecord
            {
                Id = "history-ui-fixture", Timestamp = DateTime.UtcNow, SourceKind = "dictation",
                RawText = "Synthetic history test.\nSecond paragraph.",
                FinalText = "Synthetic history test.\n\nSecond paragraph for editing and export.",
                AppName = "UI test fixture", AppProcessName = "fixture", Language = "en",
                EngineUsed = "fixture", ModelUsed = "synthetic-model", TranscriptionTaskUsed = "transcribe"
            };
            if (Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_HISTORY_AUDIO_FIXTURE") == "1")
            {
                // A quiet one-second generated tone, only in the ephemeral History fixture.
                // This exercises the real store and detail actions without recording a microphone.
                var samples = Enumerable.Range(0, 16000).Select(index => (float)(0.03 * Math.Sin(2 * Math.PI * 440 * index / 16000))).ToArray();
                historyService.TryAddRecordWithAudio(fixtureRecord with
                {
                    RawText = "Synthetic history audio test.", FinalText = "Synthetic history audio test.\n\nA generated tone was saved with this entry. No microphone was recorded.",
                    DurationSeconds = 1
                }, samples, 16000, () => true);
                var missing = historyService.TryAddRecordWithAudio(fixtureRecord with
                {
                    Id = "history-missing-audio-fixture", RawText = "Missing audio test.", FinalText = "Missing audio test.\n\nThe generated test audio was removed. This transcript remains available.", DurationSeconds = 1
                }, samples, 16000, () => true);
                if (historyService.ResolveAudioPath(missing.Record.AudioFileName) is { } missingPath) File.Delete(missingPath);
            }
            else historyService.TryAddRecord(fixtureRecord);
        }
#endif
        HistoryView.Connect(new TypeWhisper.Presentation.HistoryReader(historyService), new TypeWhisper.Presentation.HistoryActions(historyService), historyService);
        _dictation = new LocalDictationSession(historyService, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _httpApi = new WinUIHttpApi(_dictation, DispatcherQueue);
        _httpApi.DataChanged += () =>
        {
            _lexicon?.RefreshApiData();
            WorkflowsView.RefreshApiData();
            if (_workflowShortcuts?.Initialize() is { } error) MetricsText.Text = error;
        };
        HistoryView.ReadTranscript = _dictation.ReadHistoryAsync;
        HistoryView.StopReading = _dictation.StopHistoryReadbackAsync;
        _dictation.StopHistoryPlayback = () => { HistoryView.StopAudioPlayback(); RecorderView.StopAudioPlayback(); };
        HistoryView.CanPlayAudio = () => !_closing && !_profileRestoreClosing && _dictation.CanChangeProvider && !_dictation.Models.Busy
            && _dictationInput?.IsRecordingOrStarting != true && _workflowTask is not { IsCompleted: false };
        HistoryView.PrepareAudioPlayback = _dictation.SpokenFeedback.CancelAndDrainAsync;
        RecorderView.CanPlayAudio = HistoryView.CanPlayAudio;
        RecorderView.PrepareAudioPlayback = async () =>
        {
            HistoryView.StopAudioPlayback();
            await _dictation.SpokenFeedback.CancelAndDrainAsync();
        };
        WorkflowsView.Connect(_dictation);
        _dictation.ReviewRequested += ShowOutputReview;
        _dictation.OutputCompleted += id => DispatcherQueue.TryEnqueue(() => _ = HideCompletedOverlayAsync(id));
        historyService.RecordsChanged += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_historyOpen) await HistoryView.RefreshAsync();
            if (_settingsWindow is not null) await _settingsWindow.RefreshActivityAsync();
        });
        PluginsView.ConfigureRuntime(_dictation);
        _dictation.Changed += () => DispatcherQueue.TryEnqueue(UpdateLiveDictation);
        HistoryView.ExitRequested += (_, _) => CloseHistory();
        RecorderView.ExitRequested += (_, _) => CloseRecorder();
        RecorderView.Connect(_dictation);
        _httpApi.RecorderRequest = RecorderView.HandleApiAsync;
        _httpApi.ImportSettings = (store, preview) => RestoreApiProfile?.Invoke(store, preview) ?? Task.CompletedTask;
        RecorderView.IsQueuedSource = path => _fileTranscription?.ContainsSource(path) == true;
        RecorderView.TranscribeRequested += path =>
        {
            CloseRecorder();
            OpenFileTranscription();
            _fileTranscription?.AddRecording(path);
        };
        HistoryView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        WorkflowsView.ExitRequested += (_, _) => CloseWorkflows();
        WorkflowsView.LauncherRequested += (_, _) => { CloseWorkflows(); SearchBox.Text = string.Empty; };
        HistoryView.LauncherRequested += (_, _) => { CloseHistory(); SearchBox.Text = string.Empty; };
        RecorderView.LauncherRequested += (_, _) => { CloseRecorder(); SearchBox.Text = string.Empty; };
        WorkflowsView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        PluginsView.ExitRequested += (_, _) => ClosePlugins();
        PluginsView.LauncherRequested += (_, _) => { ClosePlugins(); SearchBox.Text = string.Empty; };
        PluginsView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        MarketplaceView.ConfigureRuntime(_dictation);
        MarketplaceView.ExitRequested += (_, _) => CloseMarketplace();
        MarketplaceView.LauncherRequested += (_, _) => { CloseMarketplace(); SearchBox.Text = string.Empty; };
        MarketplaceView.ClearSearchRequested += (_, _) => SearchBox.Text = string.Empty;
        MarketplaceView.DetailModeChanged += detail =>
        {
            if (!_marketplaceOpen) return;
            SearchBox.IsEnabled = !detail;
            SearchSurface.IsHitTestVisible = !detail;
            SearchSurface.Opacity = detail ? 0.5 : 1;
        };
        MarketplaceView.ManageRequested += async id =>
        {
            CloseMarketplace();
            SearchBox.Text = string.Empty;
            OpenPlugins();
            await PluginsView.OpenInstalledAsync(id);
        };
        PluginsView.MarketplaceRequested += (_, _) => SwitchIntegrationTab(discover: true);
        MarketplaceView.RestartRequested = RestartForPluginUpdateAsync;
        PluginsView.RestartRequested = RestartForPluginUpdateAsync;
        MarketplaceView.InstalledRequested += (_, _) => SwitchIntegrationTab(discover: false);
        PluginsView.ReturnToDictationRequested += (_, _) =>
        {
            if (_pluginsOpen) ClosePlugins();
            OpenSettings();
        };
        PluginsView.DetailModeChanged += detail =>
        {
            if (!_pluginsOpen) return;
            SearchBox.IsEnabled = !detail;
            SearchSurface.IsHitTestVisible = !detail;
            SearchSurface.Opacity = detail ? 0.5 : 1;
        };
        WorkflowsView.ConfigurationModeChanged += editing =>
        {
            SearchBox.IsEnabled = !editing;
            SearchSurface.IsHitTestVisible = !editing;
            SearchSurface.Opacity = editing ? 0.5 : 1;
        };
        SearchSurface.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(SearchSurface_PointerPressed),
            handledEventsToo: true);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        NativeWindowAppearance.RemoveSystemBorder(this);
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };

        try
        {
            _hotkeyRegistration = new HotkeyRegistration(this, ShowFromHotkey);
            var savedHotkey = File.Exists(LauncherHotkeyPath) ? File.ReadAllText(LauncherHotkeyPath) : "Alt+Space";
            var hotkeyError = _hotkeyRegistration.TryChange(savedHotkey);
            if (hotkeyError is not null) MetricsText.Text = hotkeyError;
            _settingsValues["QuickLaunchHotkeys"] = _hotkeyRegistration.Value;
            HotkeyHint.Text = _hotkeyRegistration.DisplayText;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MetricsDot.Fill = new SolidColorBrush(Colors.Orange);
            MetricsText.Text = $"Alt+Space unavailable · {exception.Message}";
        }

        foreach (var command in Commands)
            FilteredItems.Add(command);

        Activated += MainWindow_Activated;
        SizeChanged += (_, _) => PositionNearTopCenter();
        AppWindow.Changed += AppWindow_Changed;
        ResizeForCurrentMonitor();
        PlaceOnInvocationMonitor();
        SelectFirstResult();
    }

    internal void ShowFromActivation()
    {
        if (_profileRestoreClosing) return;
        if (!_closing && !_historyOpen && !_recorderOpen && !_workflowsOpen &&
            !_pluginsOpen && !_marketplaceOpen && !LexiconOpen && !FileTranscriptionOpen)
            MetricsText.Text = DictationStatusForDisplay;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        PlaceOnInvocationMonitor();
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            presenter.IsAlwaysOnTop = true;
        }
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ShowFromHotkey()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.IsVisible && GetForegroundWindow() == hwnd)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = false;
            AppWindow.Hide();
            return;
        }
        ShowFromActivation();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_closing || _profileRestoreClosing) return;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
            return;
        }

        if (_activationStopwatch.IsRunning)
        {
            _activationStopwatch.Stop();
            Debug.WriteLine($"Main window first activation: {_activationStopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        }
        NativeWindowAppearance.RemoveSystemBorder(this);
        // Closing a settings picker reactivates the window. Keep its current
        // field focused instead of jumping back to the name and scrolling up.
        if (_workflowsOpen && WorkflowsView.IsConfiguring) return;
        if (_pluginsOpen && PluginsView.IsDetail) return;
        if (_marketplaceOpen && MarketplaceView.IsDetail) return;
        if (_recorderOpen) RecorderView.FocusEntry();
        else if (_workflowsOpen && WorkflowsView.IsDetail) WorkflowsView.FocusEntry();
        else SearchBox.Focus(FocusState.Programmatic);
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closing || _profileRestoreClosing) return;
        if (args.DidVisibilityChange)
            RecorderView.SetPresented(_recorderOpen && sender.IsVisible);
        if (!args.DidPositionChange)
            return;

        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (dpi == 0 || dpi == _appliedWindowDpi)
            return;

        ResizeForCurrentMonitor();
        PositionNearTopCenter();
    }

    private void ResizeForCurrentMonitor(RectInt32? targetWorkArea = null)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;

        _appliedWindowDpi = dpi;
        var logicalWidth = CompactWidth;
        var logicalHeight = CompactHeight;
        var scale = dpi / 96d;
        var width = (int)Math.Round(logicalWidth * scale);
        var height = (int)Math.Round(logicalHeight * scale);
        if (targetWorkArea is { } workArea)
        {
            var margin = (int)Math.Round(24 * scale);
            width = Math.Min(width, Math.Max(320, workArea.Width - margin * 2));
            height = Math.Min(height, Math.Max(360, workArea.Height - margin * 2));
        }
        AppWindow.Resize(new SizeInt32(
            width,
            height));
    }

    private void PlaceOnInvocationMonitor()
    {
        var area = ResolveInvocationDisplayArea();
        if (area is null)
            return;

        // Move the hidden window into the target work area first so Windows
        // reports that monitor's effective DPI before the final resize.
        AppWindow.Move(new PointInt32(
            area.WorkArea.X + area.WorkArea.Width / 2,
            area.WorkArea.Y + 32));
        ResizeForCurrentMonitor(area.WorkArea);
        PositionNearTopCenter(area);
    }

    private DisplayArea? ResolveInvocationDisplayArea()
    {
        var ownWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero && foregroundWindow != ownWindow)
        {
            var foregroundId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(foregroundWindow);
            var foregroundArea = DisplayArea.GetFromWindowId(foregroundId, DisplayAreaFallback.None);
            if (foregroundArea is not null)
                return foregroundArea;
        }

        if (GetCursorPos(out var cursorPosition))
        {
            var cursorArea = DisplayArea.GetFromPoint(
                new PointInt32(cursorPosition.X, cursorPosition.Y),
                DisplayAreaFallback.None);
            if (cursorArea is not null)
                return cursorArea;
        }

        return DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
    }

    private void PositionNearTopCenter(DisplayArea? requestedArea = null)
    {
        var area = requestedArea ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (area is null)
            return;

        var work = area.WorkArea;
        var size = AppWindow.Size;
        var x = work.X + Math.Max(0, (work.Width - size.Width) / 2);
        var scale = Math.Max(1d, _appliedWindowDpi / 96d);
        var y = work.Y + Math.Max((int)Math.Round(28 * scale), (work.Height - size.Height) / 7);
        AppWindow.Move(new PointInt32(x, y));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
            _isSearchEditing = true;
        UpdateSearchPresentation();

        if (_recorderOpen)
        {
            return;
        }

        if (_historyOpen)
        {
            HistoryView.Filter(query);
            return;
        }

        if (_workflowsOpen)
        {
            WorkflowsView.Filter(query);
            return;
        }

        if (_pluginsOpen)
        {
            PluginsView.Filter(query);
            return;
        }

        if (_marketplaceOpen)
        {
            MarketplaceView.Filter(query);
            return;
        }

        var matches = string.IsNullOrEmpty(query)
            ? Commands
            : Commands.Where(command =>
                command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(command => string.Equals(command.Title, query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(command => command.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        FilteredItems.Clear();
        foreach (var command in matches)
            FilteredItems.Add(command);
        sw.Stop();

        CompactSectionLabel.Text = string.IsNullOrEmpty(query) ? "ACTIVE & PINNED" : $"{FilteredItems.Count} RESULTS";
        MetricsText.Text = $"Local search · {sw.Elapsed.TotalMilliseconds:0.00} ms · {FilteredItems.Count} results";
        MetricsDot.Fill = new SolidColorBrush(sw.Elapsed.TotalMilliseconds <= 16 ? Colors.MediumSeaGreen : Colors.OrangeRed);
        SelectFirstResult();
    }

    private void SearchSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginSearchEditing();
    }

    private void BeginSearchEditing()
    {
        _isSearchEditing = true;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Pointer);
        SearchBox.SelectionStart = SearchBox.Text.Length;
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text))
            _isSearchEditing = false;
        UpdateSearchPresentation();
    }

    private void UpdateSearchPresentation()
    {
        if (SearchPlaceholder is null)
            return;

        var showPlaceholder = string.IsNullOrEmpty(SearchBox.Text) && !_isSearchEditing;
        SearchPlaceholder.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Opacity = showPlaceholder ? 0 : 1;
    }

    private void SelectFirstResult()
    {
        if (FilteredItems.Count == 0)
        {
            UpdateDetail(null);
            return;
        }

        CompactResults.SelectedIndex = 0;
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItem: Command command })
        {
            _selected = command;
            UpdateDetail(command);
        }
    }

    private void UpdateDetail(Command? command)
    {
        _selected = command;
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Command command)
        {
            _selected = command;
            RunSelected();
        }
    }

    private void RunSelected()
    {
        if (FileTranscriptionOpen || LexiconOpen) return;
        ActionPanel.Visibility = Visibility.Collapsed;
        if (_recorderOpen) return;
        if (_marketplaceOpen)
            MarketplaceView.OpenSelected();
        else if (_pluginsOpen)
            PluginsView.OpenSelected();
        else if (_workflowsOpen)
            WorkflowsView.OpenSelected();
        else if (_historyOpen)
            HistoryView.OpenSelected();
        else if (_selected?.Title == "Read last transcription")
            ReadLastTranscription();
        else if (_selected?.Title == "Copy last transcription")
            CopyLastTranscription();
        else if (_selected?.Title == "History")
            OpenHistory();
        else if (_selected?.Title == "Recorder")
            OpenRecorder();
        else if (_selected?.Title == "Workflows")
            OpenWorkflows();
        else if (_selected?.Title == "Integrations")
            OpenPlugins();
        else if (_selected?.Title == "Transcribe file")
            OpenFileTranscription();
        else if (_selected?.Title == "Dictionary")
            OpenLexicon();
        else if (_selected?.Title == "Snippets")
            OpenLexicon(true);
        else if (_selected?.Title == "Settings")
            OpenSettings();
        else if (_selected?.Title is "Dashboard" or "Statistics")
            OpenDashboard(_selected.Title == "Statistics");
        else if (_selected?.Title.Contains("dictation", StringComparison.OrdinalIgnoreCase) == true)
            MetricsText.Text = DictationStatusForDisplay;
        else if (_selected is not null)
            MetricsText.Text = $"Executed {_selected.Title} · preview data only";
    }

    private void ToggleActions()
    {
        if (_historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen) return;
        ActionPanel.Visibility = ActionPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ShowWaveformOverlay(DisplayArea? targetArea = null)
    {
        try
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(_transcriptPreviewEnabled);
                _overlay.Closed += (_, _) => _overlay = null;
            }
            var area = targetArea ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            _overlay.SetLayout(OverlayPreferences);
            _overlay.SetMode(_overlayMode, area);
            _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
            _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
            _overlay.ActivateWithoutTakingFocus();
            OverlayPreviewPanel.Visibility = _historyOpen || _recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen ? Visibility.Collapsed : Visibility.Visible;
            UpdateOverlayControls();
            MetricsText.Text = _overlayMode == OverlayMode.Minimal
                ? "Overlay active · minimal indicator"
                : _transcriptPreviewEnabled
                ? "Overlay active · live transcript preview on"
                : "Overlay active · live transcript preview off";
        }
        catch (Exception exception)
        {
            _overlay = null;
            MetricsDot.Fill = new SolidColorBrush(Colors.OrangeRed);
            MetricsText.Text = $"Overlay error · {exception.GetType().Name}: {exception.Message}";
        }
    }

    private void WindowRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_historyOpen)
        {
            HistoryView.HandleActionKey(e);
            if (e.Handled) return;
        }
        if (LexiconOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Back && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not TextBox)
            { _lexicon?.GoBack(); e.Handled = true; }
            return;
        }
        if (FileTranscriptionOpen)
        {
            _fileTranscription?.HandleActionKey(e);
            if (e.Handled) return;
            if (e.Key == global::Windows.System.VirtualKey.Back && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not TextBox)
            {
                _fileTranscription?.GoBack(); e.Handled = true;
            }
            return;
        }
        if ((!_historyOpen && !_recorderOpen && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen) || e.Key != global::Windows.System.VirtualKey.Back) return;
        // Inspect before the editor processes deletion: deleting the last character
        // must not also navigate away from the current page.
        var focused = FocusManager.GetFocusedElement(WindowRoot.XamlRoot);
        if (_workflowsOpen && WorkflowsView.IsConfiguring && focused is TextBox) return;
        if (focused is TextBox { Text.Length: > 0 } or TextBox { AcceptsReturn: true } or PasswordBox or RichEditBox) return;
        foreach (var modifier in new[] { global::Windows.System.VirtualKey.Control, global::Windows.System.VirtualKey.Menu,
                     global::Windows.System.VirtualKey.Shift, global::Windows.System.VirtualKey.LeftWindows, global::Windows.System.VirtualKey.RightWindows })
        {
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(modifier)
                .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        }
        if (_recorderOpen) RecorderView.GoBack();
        else if (_marketplaceOpen) MarketplaceView.GoBack();
        else if (_pluginsOpen) PluginsView.GoBack();
        else if (_workflowsOpen) WorkflowsView.GoBack();
        else HistoryView.GoBack();
        e.Handled = true;
    }

    private void WindowRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (LexiconOpen)
        {
            if (e.Key == global::Windows.System.VirtualKey.Escape) { _lexicon?.GoBack(); e.Handled = true; }
            return;
        }
        if (FileTranscriptionOpen)
        {
            _fileTranscription?.HandleActionKey(e);
            if (e.Handled) return;
            if (e.Key == global::Windows.System.VirtualKey.Escape) { _fileTranscription?.GoBack(); e.Handled = true; }
            return;
        }
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control)
            .HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == (global::Windows.System.VirtualKey)0xBC) // VK_OEM_COMMA
        {
            OpenSettings();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == global::Windows.System.VirtualKey.K)
        {
            ToggleActions();
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            if (_recorderOpen)
                RecorderView.GoBack();
            else if (_marketplaceOpen && MarketplaceView.IsDetail)
                MarketplaceView.GoBack();
            else if (_pluginsOpen && PluginsView.IsDetail)
                PluginsView.GoBack();
            else if (_workflowsOpen && WorkflowsView.IsDetail)
                WorkflowsView.GoBack();
            else if (_historyOpen && (HistoryView.IsReading || HistoryView.IsSelecting))
                HistoryView.GoBack();
            else if (ActionPanel.Visibility == Visibility.Visible)
                ActionPanel.Visibility = Visibility.Collapsed;
            else if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                _isSearchEditing = false;
                SearchBox.Text = string.Empty;
            }
            else if (_historyOpen)
                CloseHistory();
            else if (_workflowsOpen)
                CloseWorkflows();
            else if (_pluginsOpen)
                ClosePlugins();
            else if (_marketplaceOpen)
                CloseMarketplace();
            else
                AppWindow.Hide();
            e.Handled = true;
            return;
        }

        if (_historyOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            HistoryView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_workflowsOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            WorkflowsView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_pluginsOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            PluginsView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (_marketplaceOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            MarketplaceView.MoveSelection(e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (!_historyOpen && !_recorderOpen && !_workflowsOpen && !_pluginsOpen && !_marketplaceOpen && ReferenceEquals(FocusManager.GetFocusedElement(WindowRoot.XamlRoot), SearchBox)
            && e.Key is global::Windows.System.VirtualKey.Down or global::Windows.System.VirtualKey.Up)
        {
            if (FilteredItems.Count > 0)
            {
                var offset = e.Key == global::Windows.System.VirtualKey.Down ? 1 : -1;
                CompactResults.SelectedIndex = Math.Clamp(CompactResults.SelectedIndex + offset, 0, FilteredItems.Count - 1);
                CompactResults.ScrollIntoView(CompactResults.SelectedItem);
            }
            e.Handled = true;
            return;
        }

        if (e.Key == global::Windows.System.VirtualKey.Enter && FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is not Button)
        {
            if (FocusManager.GetFocusedElement(WindowRoot.XamlRoot) is TextBox { AcceptsReturn: true }) return;
            RunSelected();
            e.Handled = true;
        }
    }

    private OverlayPreferences _layoutPreferences = new(OverlayMode.Standard, true, false);
    private readonly Dictionary<string, string> _settingsValues = new();
    private OverlayPreferences OverlayPreferences => _layoutPreferences with { Mode = _overlayMode, LiveText = _transcriptPreviewEnabled, TechnicalDetails = _technicalDetailsEnabled };
    private static readonly string OverlayPreferencesPath = WinUIProfile.DataPath("overlay.json");
    private void LoadOverlayPreferences()
    {
        try
        {
            if (!File.Exists(OverlayPreferencesPath)) return;
            var preferences = OverlayPreferencesStore.Read(OverlayPreferencesPath);
            _layoutPreferences = preferences;
            _overlayMode = preferences.Mode; _transcriptPreviewEnabled = preferences.LiveText;
            _technicalDetailsEnabled = preferences.TechnicalDetails;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MetricsText.Text = "Could not load overlay preferences: " + ex.Message; }
    }
    private bool SaveOverlayPreferences(OverlayPreferences? preferences = null)
    {
        try
        {
            OverlayPreferencesStore.Save(OverlayPreferencesPath, preferences ?? OverlayPreferences);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MetricsText.Text = "Could not save overlay preferences: " + ex.Message; return false; }
    }

    internal void OpenSetup()
    {
        OpenSettings();
        _settingsWindow?.ShowSetup();
    }

    internal void OpenLexicon(bool snippets = false)
    {
        if (_lexicon is null)
        {
            _lexicon = new LexiconView();
            _lexicon.ConnectTraining(_dictation);
            _lexicon.ExitRequested += () =>
            {
                LexiconHost.Visibility = Visibility.Collapsed;
                SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
                OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Focus(FocusState.Programmatic);
            };
            LexiconHost.Child = _lexicon;
        }
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        LexiconHost.Visibility = Visibility.Visible;
        _lexicon.Present(snippets);
    }

    private void EnsureFileTranscription()
    {
        if (_fileTranscription is null)
        {
            _fileTranscription = new FileTranscriptionView();
            _fileTranscription.Connect(_dictation);
            _fileTranscription.ExitRequested += () =>
            {
                FileTranscriptionHost.Visibility = Visibility.Collapsed;
                SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
                OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
                SearchBox.Focus(FocusState.Programmatic);
            };
            FileTranscriptionHost.Child = _fileTranscription;
        }
    }

    internal void OpenFileTranscription()
    {
        EnsureFileTranscription();
        SearchSurface.Visibility = CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        FileTranscriptionHost.Visibility = Visibility.Visible;
        _fileTranscription!.Present();
    }

    internal void OpenSettings()
    {
        if (_profileRestoreClosing) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(OverlayPreferences, _settingsValues);
            _settingsWindow.SetLiveTranscriptionAvailability(_dictation.SupportsLiveTranscription);
            _settingsWindow.CommitLauncherHotkeys = ChangeLauncherHotkeys;
            _settingsWindow.CommitRecentTranscriptionsHotkeys = ChangeHistoryShortcut;
            _settingsWindow.CommitCopyLastTranscriptionHotkeys = ChangeCopyLastShortcut;
            _settingsWindow.CommitReadLastTranscriptionHotkeys = ChangeReadLastShortcut;
            _settingsWindow.CommitDictationHotkeys = ChangeDictationHotkeys;
            _settingsWindow.CommitCancelProcessingHotkeys = value =>
            {
                if (_closing || _profileRestoreClosing) return "The app is shutting down.";
                if (_cancelProcessingHotkey is null) return "Cancel shortcuts are unavailable. Wait for startup to finish or restart the app.";
                if (_workflowShortcuts?.Conflict(value) is { } conflict) return conflict;
                if (HistoryShortcutConflict(value) is { } historyConflict) return historyConflict;
                if (CopyLastShortcutConflict(value) is { } copyConflict) return copyConflict;
                if (ReadLastShortcutConflict(value) is { } readConflict) return readConflict;
                var error = _cancelProcessingHotkey.TryChange(value);
                _settingsValues["CancelProcessingHotkeys"] = _cancelProcessingHotkey.Value;
                return error;
            };
            _settingsWindow.CreateSetupWizard = exit => new SetupWizard(_settingsValues, exit,
                value => _closing || _profileRestoreClosing ? "The app is shutting down." : ChangeDictationHotkeys(value),
                _dictation, OpenProviderSettings);
            var dictationSettings = new LiveDictationSettings(_dictation, OpenProviderSettings);
            var startup = WindowsStartupRegistration.Create();
            DictationRecoveryView? recoveryView = null;
            _settingsWindow.ConfigureLiveSettings = (category, content, pickers) =>
            {
                dictationSettings.Configure(category, content, pickers);
                LiveStartupSettings.Configure(category, content, pickers, startup);
                if (category == "Advanced")
                {
                    content.Children.Clear(); pickers.Clear();
                    content.Children.Add(new TextBlock { Text = "Advanced", FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
                    content.Children.Add(new HttpApiSettingsView(_httpApi));
                    content.Children.Add(new TextBlock { Text = "Integrations", FontSize = 18, Margin = new Thickness(0, 16, 0, 0) });
                    content.Children.Add(new RaycastIntegrationView());
                }
                if (category == "Shortcuts" && _cancelProcessingHotkey?.Error is { } shortcutError)
                    content.Children.Add(new TextBlock { Text = shortcutError, TextWrapping = TextWrapping.Wrap });
                if (category == "Files & recovery")
                {
                    content.Children.Clear(); pickers.Clear();
                    recoveryView ??= CreateRecoveryView();
                    content.Children.Add(recoveryView);
                    _ = recoveryView.PresentAsync();
                }
            };
            _settingsWindow.RestoreProfile = RestoreProfile;
            _settingsWindow.ConfigureActivity = activity => activity.Connect(_dictation.HistoryReader, () => _dictation.OutputPreferences.Current.SaveToHistory);
            _settingsWindow.HistoryRequested += () =>
            {
                if (_historyOpen) { _settingsWindow?.AppWindow.Hide(); ShowFromActivation(); return; }
                if (_recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen)
                {
                    _settingsWindow?.ShowHistoryNavigationHint();
                    return;
                }
                _settingsWindow?.AppWindow.Hide(); ShowFromActivation(); OpenHistory();
            };
            _settingsWindow.Closed += (_, _) =>
            {
                CloseRecoveryView(recoveryView);
                _settingsWindow = null;
            };
            _settingsWindow.PreferencesChanged += preferences =>
            {
                if (!SaveOverlayPreferences(preferences))
                {
                    _settingsWindow.SetPreferences(OverlayPreferences);
                    _settingsWindow.ShowOverlaySaveError(MetricsText.Text);
                    return;
                }
                var modeChanged = _overlayMode != preferences.Mode || _layoutPreferences.Anchor != preferences.Anchor
                    || _layoutPreferences.Left != preferences.Left || _layoutPreferences.Right != preferences.Right;
                _layoutPreferences = preferences;
                _overlayMode = preferences.Mode;
                _transcriptPreviewEnabled = preferences.LiveText;
                _technicalDetailsEnabled = preferences.TechnicalDetails;
                if (_liveOverlay?.IsPreviewVisible == true) UpdateLiveDictation();
                if (_overlay?.IsPreviewVisible == true)
                {
                    // Keep the existing overlay's monitor and bottom anchor.
                    // A text-only toggle must not resize or move its lower block.
                    _overlay.SetLayout(preferences);
                    if (modeChanged)
                        _overlay.SetMode(_overlayMode, DisplayArea.GetFromWindowId(_overlay.AppWindow.Id, DisplayAreaFallback.Primary));
                    _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
                    _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
                }
                UpdateOverlayControls();
            };
            _settingsWindow.PreviewRequested += (_, _) =>
            {
                if (_overlay?.IsPreviewVisible == true) EndPreview_Click(this, new RoutedEventArgs());
                else ShowWaveformOverlay(DisplayArea.GetFromWindowId(_settingsWindow!.AppWindow.Id, DisplayAreaFallback.Primary));
            };
        }
        _settingsWindow.SetPreferences(OverlayPreferences);
        _settingsWindow.SetPreviewVisible(_overlay?.IsPreviewVisible == true);
        _settingsWindow.ShowOn(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
    }

    internal void OpenDashboard(bool statistics = false)
    {
        OpenSettings(); _settingsWindow!.ShowActivity(statistics);
    }

    internal void OpenSyncBackup()
    {
        OpenSettings(); _settingsWindow!.ShowSyncBackup();
    }

    internal void OpenAccount()
    {
        OpenSettings(); _settingsWindow!.ShowAccount();
    }

    internal void OpenSelectComparison()
    {
        OpenSettings();
        _settingsWindow!.ShowSelectComparison();
    }

    private void SwitchIntegrationTab(bool discover)
    {
        var launcherQuery = _launcherQuery;
        if (_pluginsOpen) ClosePlugins();
        if (_marketplaceOpen) CloseMarketplace();
        if (discover) OpenMarketplace(); else OpenPlugins();
        _launcherQuery = launcherQuery;
    }

    private async void OpenProviderSettings(string pluginId)
    {
        if (_historyOpen || _recorderOpen || _workflowsOpen || LexiconOpen || FileTranscriptionOpen)
        {
            _settingsWindow?.ShowIntegrationNavigationHint();
            return;
        }
        _settingsWindow?.AppWindow.Hide();
        ShowFromActivation();
        if (_marketplaceOpen) SwitchIntegrationTab(discover: false);
        else if (!_pluginsOpen) OpenPlugins();
        await PluginsView.OpenProviderSettingsAsync(pluginId);
    }

    private void OpenMarketplace()
    {
        _launcherQuery = SearchBox.Text;
        _marketplaceOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        MarketplaceView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search plugins by name or purpose…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Marketplace search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        MarketplaceView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseMarketplace()
    {
        MarketplaceView.ResetNavigation();
        _marketplaceOpen = false;
        MarketplaceView.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = SearchSurface.IsHitTestVisible = true;
        SearchSurface.Opacity = 1;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Integrations");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenPlugins()
    {
        _launcherQuery = SearchBox.Text;
        _pluginsOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        PluginsView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search plugins by name or category…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Plugin search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        PluginsView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ClosePlugins()
    {
        _pluginsOpen = false;
        PluginsView.EndSetupNavigation();
        PluginsView.Visibility = Visibility.Collapsed;
        SearchBox.IsEnabled = SearchSurface.IsHitTestVisible = true;
        SearchSurface.Opacity = 1;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Integrations");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenWorkflows()
    {
        _launcherQuery = SearchBox.Text;
        _workflowsOpen = true;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        WorkflowsView.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Search workflows by name or purpose…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Workflow search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        WorkflowsView.Filter(string.Empty);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseWorkflows()
    {
        _workflowsOpen = false;
        WorkflowsView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var command = FilteredItems.FirstOrDefault(item => item.Title == "Workflows");
        if (command is not null) { CompactResults.SelectedItem = command; UpdateDetail(command); }
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    internal void ShowHistoryFromTray()
    {
        ShowFromActivation();
        if (_historyOpen) return;
        if (_recorderOpen || _workflowsOpen || _pluginsOpen || _marketplaceOpen || LexiconOpen || FileTranscriptionOpen)
        {
            MetricsText.Text = "Return to Quick Launch before opening History.";
            return;
        }
        OpenHistory();
    }

    private void OpenHistory()
    {
        _ = HistoryView.RefreshAsync();
        _launcherQuery = SearchBox.Text;
        _historyOpen = true;
        CommandSurface.Visibility = Visibility.Collapsed;
        QuickLaunchFooter.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        SearchPlaceholder.Text = "Search history by title or transcript…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "History search");
        SearchBox.Text = string.Empty;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        HistoryView.Filter(string.Empty);
        NavigationHint.Text = "↑↓ Navigate   Enter Open   ⌫ / Esc Back";
        MetricsText.Text = "History · local data";
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseHistory()
    {
        HistoryView.StopAudioPlayback();
        HistoryView.StopReadback();
        _historyOpen = false;
        HistoryView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = Visibility.Visible;
        QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var historyCommand = FilteredItems.FirstOrDefault(command => command.Title == "History");
        if (historyCommand is not null)
        {
            CompactResults.SelectedItem = historyCommand;
            UpdateDetail(historyCommand);
        }
        NavigationHint.Text = "↑↓ Navigate   Enter Run   Ctrl K Actions   Esc Hide";
        MetricsText.Text = "Quick Launch";
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OpenRecorder()
    {
        _launcherQuery = SearchBox.Text;
        _recorderOpen = true;
        CommandSurface.Visibility = Visibility.Collapsed;
        QuickLaunchFooter.Visibility = Visibility.Collapsed;
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        RecorderView.Visibility = Visibility.Visible;
        RecorderView.SetPresented(true);
        SearchSurface.Visibility = Visibility.Collapsed;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        RecorderView.FocusEntry();
    }

    private void CloseRecorder()
    {
        SearchSurface.Visibility = Visibility.Visible;
        _recorderOpen = false;
        RecorderView.SetPresented(false);
        RecorderView.Visibility = Visibility.Collapsed;
        CommandSurface.Visibility = Visibility.Visible;
        QuickLaunchFooter.Visibility = Visibility.Visible;
        OverlayPreviewPanel.Visibility = _overlay?.IsPreviewVisible == true ? Visibility.Visible : Visibility.Collapsed;
        SearchPlaceholder.Text = "Search commands, recordings, workflows…";
        SearchGlyph.Kind = "search";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SearchBox, "Quick Launch search");
        SearchBox.Text = _launcherQuery;
        var recorder = FilteredItems.FirstOrDefault(command => command.Title == "Recorder");
        if (recorder is not null) CompactResults.SelectedItem = recorder;
        _isSearchEditing = false;
        UpdateSearchPresentation();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        ((OverlappedPresenter)AppWindow.Presenter).Minimize();
    private void HideButton_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();
    private void TestWaveformButton_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        if (TrayProbeEnabled)
        {
            if (!_closing && !_profileRestoreClosing) TrayProbeRequested?.Invoke();
            return;
        }
#endif
        ShowWaveformOverlay();
    }
    private void OverlayMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string mode } && Enum.TryParse<OverlayMode>(mode, out var selected))
        {
            _overlayMode = selected;
            SaveOverlayPreferences();
            ShowWaveformOverlay();
        }
    }

    private void PausePreview_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.TogglePaused();
        UpdateOverlayControls();
    }

    private void EndPreview_Click(object sender, RoutedEventArgs e)
    {
        _overlay?.HidePreview();
        OverlayPreviewPanel.Visibility = Visibility.Collapsed;
        MetricsText.Text = "Overlay preview ended";
        UpdateOverlayControls();
    }

    private void UpdateOverlayControls()
    {
        _settingsWindow?.SetPreferences(OverlayPreferences);
        _settingsWindow?.SetPreviewVisible(_overlay?.IsPreviewVisible == true);
        foreach (var button in new[] { StandardOverlayButton, CompactOverlayButton, MinimalOverlayButton })
        {
            var selected = (string)button.Tag == _overlayMode.ToString();
            button.Style = (Style)Application.Current.Resources[selected
                ? "PrimaryButtonStyle" : "SecondaryButtonStyle"];
        }
        PausePreviewButton.Content = _overlay?.IsPaused == true ? "Resume" : "Pause";
        var minimal = _overlayMode == OverlayMode.Minimal && _overlay?.IsPreviewVisible == true;
        UpdateTranscriptToggle();
        OverlayPreviewHint.Text = minimal
            ? "Minimal: indicator only at the screen edge · live-text preference remembered"
            : "Microphone level only · transcript is sample text · no audio saved";
    }
    private void UpdateTranscriptToggle()
    {
        var minimal = _overlayMode == OverlayMode.Minimal && _overlay?.IsPreviewVisible == true;
        var available = _dictation.SupportsLiveTranscription;
        TranscriptToggleButton.IsEnabled = !minimal && available;
        TranscriptToggleButton.Content = !available ? "Live text · Unavailable" : minimal ? "Live text  —" : _transcriptPreviewEnabled ? "Live text  On" : "Live text  Off";
        ToolTipService.SetToolTip(TranscriptToggleButton, available ? "Show text during recording." :
            "Live transcription is unavailable for the selected provider or task. Text arrives after recording stops.");
        _settingsWindow?.SetLiveTranscriptionAvailability(available);
    }
    private void TranscriptToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_dictation.SupportsLiveTranscription) return;
        _transcriptPreviewEnabled = !_transcriptPreviewEnabled;
        SaveOverlayPreferences();
        TranscriptToggleButton.Content = _transcriptPreviewEnabled ? "Live text  On" : "Live text  Off";
        TranscriptToggleButton.Foreground = (Brush)Application.Current.Resources[
            _transcriptPreviewEnabled ? "AccentBrush" : "MutedBrush"];
        _overlay?.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
        _liveOverlay?.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
        UpdateOverlayControls();
        MetricsText.Text = _transcriptPreviewEnabled
            ? "Live transcript preview enabled"
            : "Live transcript preview disabled";
    }
    private void RunSelectedButton_Click(object sender, RoutedEventArgs e) => RunSelected();
    private void ShowActionsButton_Click(object sender, RoutedEventArgs e) => ToggleActions();
    private void KeepOpenButton_Click(object sender, RoutedEventArgs e)
    {
        RunSelected();
        SearchBox.Focus(FocusState.Programmatic);
    }
    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        ActionPanel.Visibility = Visibility.Collapsed;
        MetricsText.Text = _selected is null ? "Nothing selected" : $"Pinned {_selected.Title} · in-memory preview";
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
