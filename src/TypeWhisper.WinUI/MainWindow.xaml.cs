using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow : Window
{
    internal nint TrayMenuHandle { set => _dictation.TrayMenuHandle = value; }
    private readonly WinUIHttpApi _httpApi;
    private DictationHotkeyRegistration? _dictationHotkey;
    private ProcessingCancelHotkeyRegistration? _cancelProcessingHotkey;
    private TypeWhisper.Presentation.DictationInputCoordinator? _dictationInput;
    private Action? _observeInputMode;
    private static string DictationHotkeyPath => WinUIProfile.DataPath("dictation-hotkeys.txt");
    private string? ChangeDictationHotkeys(string value)
    {
        if (_closing || _profileRestoreClosing) return "The app is shutting down.";
        if (_dictationHotkey is null) return "Dictation hotkeys are unavailable. Restart the app.";
        if (_cancelProcessingHotkey?.ConflictWithDictation(value) is { } conflict) return conflict;
        if (_workflowShortcuts?.Conflict(value, modifierOnly: true) is { } workflowConflict) return workflowConflict;
        if (RecordingShortcutConflict(value, true) is { } recordingConflict) return recordingConflict;
        if (RecorderShortcutConflict(value, true) is { } recorderConflict) return recorderConflict;
        if (WorkflowPaletteShortcutConflict(value, modifierOnly: true) is { } paletteConflict) return paletteConflict;
        if (HistoryShortcutConflict(value, modifierOnly: true) is { } historyConflict) return historyConflict;
        if (CopyLastShortcutConflict(value, modifierOnly: true) is { } copyConflict) return copyConflict;
        if (PasteLastShortcutConflict(value, modifierOnly: true) is { } pasteConflict) return pasteConflict;
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
                async () =>
                {
                    _dictation.LivePreviewEnabled = _transcriptPreviewEnabled;
                    ShowTaskStartError(await _dictation.StartAsync());
                },
                _dictation.StopAsync, _dictation.CancelAsync,
                () => _dictation.IsRecording, () => !DictationHotkeysPaused && _dictation.CanStartFromShortcut,
                () => _dictation.RecordingModePreferences.Current,
                dispatch: action => DispatcherQueue.TryEnqueue(() => action()),
                reportError: error => System.Diagnostics.Debug.WriteLine("Dictation input failed: " + error.GetType().Name));
            _observeInputMode = () =>
            {
                if (DispatcherQueue.HasThreadAccess) _dictationInput.ObserveMode();
                else DispatcherQueue.TryEnqueue(() => _dictationInput.ObserveMode());
            };
            _dictation.Changed += _observeInputMode;
            _dictationHotkey = new DictationHotkeyRegistration(this, DispatchRecordingShortcut, () => _dictationInput.IsRecordingOrStarting, () => _dictation.RecordingModePreferences.Current,
                () => DictationHotkeysPaused);
            var saved = File.Exists(DictationHotkeyPath) ? File.ReadAllText(DictationHotkeyPath) : LocalDictationSession.DefaultShortcut;
            var error = _dictationHotkey.TryChange(saved);
            _settingsValues["MainDictationHotkeys"] = _dictationHotkey.Value;
            _dictation.Shortcut = string.IsNullOrEmpty(_dictationHotkey.Value) ? "No shortcut assigned" : _dictationHotkey.Value;
            // A shortcut taken by another app must not block the session, API, licensing or the
            // other shortcuts; the user can assign a different chord in Settings without restarting.
            var hotkeyError = error is null ? null : error + " Assign a different dictation shortcut in Settings.";
            if (hotkeyError is not null) DictationChanged?.Invoke(hotkeyError, false);
            string? cancelError;
            try
            {
                _cancelProcessingHotkey = new(this, () => CanCancelProcessing, RequestProcessingCancellation,
                    () => _dictationHotkey?.Value ?? "");
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
            InitializePasteLastShortcut();
            InitializeReadLastShortcut();
            InitializeWorkflowPaletteShortcut();
            InitializeRecordingShortcuts();
            InitializeRecorderShortcut();
            InitializeEscapeCancel();
            InitializeHotkeyRecovery();
            await WinUILicensing.ValidateAsync();
            if (!_closing) await WinUIPremiumAccount.RefreshAsync();
            if (!_closing)
            {
                WinUICloudSync.DataChanged += () => _lexicon?.RefreshApiData();
                WinUICloudSync.Initialize(DispatcherQueue);
            }
            if ((hotkeyError ?? cancelError) is { } notice && !_closing) ShowNotice(new AppNotice(notice));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { if (!_closing) ShowNotice(new AppNotice("Dictation startup failed: " + ex.Message)); }
    }

    internal void FinishDictationFromTray()
    {
        if (_dictationInput is { IsRecordingOrStarting: true } input)
            _ = input.SubmitAsync(TypeWhisper.Presentation.DictationInputAction.Stop);
    }
    internal bool CanCancelProcessing => !_closing && (_dictation.CanCancelProcessing || _workflowCancellation is not null);
    internal async Task CancelProcessingAsync()
    {
        if (!CanCancelProcessing) return;
        RequestWorkflowCancellation();
        try { if (_dictation.CanCancelProcessing) await _dictation.CancelAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { System.Diagnostics.Trace.TraceError("Processing cancellation failed: {0}", ex); if (!_closing) ShowNotice(new AppNotice("Could not finish cancellation. Try again.")); }
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
        _hotkeyRecovery?.Dispose();
        _cancelProcessingHotkey?.Dispose();
        _historyHotkey?.Dispose();
        _workflowPaletteHotkey?.Dispose();
        DisposeRecordingShortcuts();
        _recorderHotkey?.Dispose();
        _copyLastHotkey?.Dispose();
        _pasteLastHotkey?.Dispose();
        _foregroundHistory?.Dispose();
        _readLastHotkey?.Dispose();
        DisposeEscapeCancel();
        await StopWorkflowShortcutsAsync();
        // The recorder owns the session gate while capturing; save it before session shutdown waits for that gate.
        var reviews = DrainReviewWindowsAsync();
        await Task.WhenAll(RecorderView.ShutdownAsync(), reviews);
        await ShutdownCoreAsync();
    }
    private Task ShutdownCoreAsync() => _shutdown.Run(async () =>
    {
        _closing = true;
        _hotkeyRecovery?.Dispose();
        await StopWorkflowShortcutsAsync();
        _cancelProcessingHotkey?.Dispose();
        _historyHotkey?.Dispose();
        _workflowPaletteHotkey?.Dispose();
        DisposeRecordingShortcuts();
        _recorderHotkey?.Dispose();
        _copyLastHotkey?.Dispose();
        _pasteLastHotkey?.Dispose();
        _foregroundHistory?.Dispose();
        _readLastHotkey?.Dispose();
        DisposeEscapeCancel();
        _dictationHotkey?.Dispose();
        _dictationInput?.Dispose();
        if (_observeInputMode is not null) _dictation.Changed -= _observeInputMode;
        var reviews = DrainReviewWindowsAsync();
        // Finish settings confirmations, preference writes and recovery retries before
        // session shutdown disposes the recovery store they use.
        await DrainRecoveryViewsAsync();
        // Begin all cancellation requests before awaiting any drain.
        var api = _httpApi.ShutdownAsync();
        var session = _dictation.ShutdownAsync();
        var files = _fileTranscription?.ShutdownAsync() ?? Task.CompletedTask;
        _historyWindow?.CloseForShutdown();
        var workflows = WorkflowsView.ShutdownAsync();
        var lexicon = _lexicon?.ShutdownAsync() ?? Task.CompletedTask;
        var setupImport = ShutdownSettingsImportsAsync();
        await Task.WhenAll(WinUIPremiumAccount.ShutdownAsync(), WinUICloudSync.ShutdownAsync(), WinUILicensing.ShutdownAsync(), api, session, files, workflows, lexicon, reviews, _profileUiDrain ?? Task.CompletedTask, _dictationInput?.Completion ?? Task.CompletedTask,
            _dictationInitialization ?? Task.CompletedTask, setupImport);
        _liveOverlay?.Close();
    });
    internal void ShowShutdownFailure()
    {
        if (RecorderView.NeedsSaveRetry)
        {
            OpenRecorder();
            ShowNotice(new AppNotice("Recording could not be saved. Retry saving in Recorder, then choose Exit again.", Duration: TimeSpan.FromSeconds(30)));
            return;
        }
        ShowNotice(new AppNotice("Shutdown could not complete cleanly. Work is stopped; see the diagnostic log for details.", Duration: TimeSpan.FromSeconds(30)));
    }
    internal bool CanRetryRecorderShutdown => RecorderView.NeedsSaveRetry;

    private void ShowLearnedCorrections(IReadOnlyList<TypeWhisper.Core.Models.LearnedDictionaryCorrection> corrections)
    {
        if (_closing || _dictation.IsRecording || _dictation.OverlayState.Phase == DictationPhase.Processing) return;
        ++_overlayRevision;
        _completedRecordingId = Guid.Empty;
        _completedPreviewExpired = true;
        HideOverlayPreview();
        _liveOverlay ??= new OverlayWindow(false, () => _dictation.IsRecording ? _dictation.CurrentLevel : 0,
            () => _dictation.OverlayState, () => _dictation.LivePreviewText);
        _liveOverlay.SetLayout(OverlayPreferences);
        _liveOverlay.ShowCorrectionFeedback(corrections, ResolveOverlayDisplayArea());
    }
    private void HideLearnedCorrections()
    {
        if (_liveOverlay?.IsCorrectionFeedbackVisible == true) _liveOverlay.HidePreview();
    }

    private void UpdateLiveDictation()
    {
        if (_closing) return;
        var revision = ++_overlayRevision;
        _settingsWindow?.SetLiveTranscriptionAvailability(_dictation.SupportsLiveTranscription);
        DictationChanged?.Invoke(_dictation.Status, _dictation.IsRecording);
        if (_liveOverlay?.IsCorrectionFeedbackVisible == true &&
            _dictation.OverlayState.Phase is not (DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error or DictationPhase.Copied or DictationPhase.LoadingModel)) return;
        if (_dictation.OverlayState.Phase != DictationPhase.Completed) _completedPreviewExpired = false;
        else if (_completedPreviewExpired) return;
        else if (OverlayPreferences.PreviewBubbleAutoHideMilliseconds == 0)
        {
            _completedPreviewExpired = true;
            _liveOverlay?.HidePreview();
            return;
        }
        if (_dictation.OverlayState.Phase is DictationPhase.Recording or DictationPhase.Processing or DictationPhase.Error or DictationPhase.Copied or DictationPhase.Completed or DictationPhase.LoadingModel
            || _dictation.OverlayState.ShowsCancelled || _dictation.OverlayState.ShowsCancelWarning)
        {
            HideOverlayPreview();
            var showTranscript = _dictation.OverlayState.ShouldShowTranscript(_overlayMode, _transcriptPreviewEnabled, _dictation.SupportsLiveTranscription);
            if (_liveOverlay is null)
                _liveOverlay = new OverlayWindow(showTranscript, () => _dictation.IsRecording ? _dictation.CurrentLevel : 0, () => _dictation.OverlayState, () => _dictation.LivePreviewText);
            // Apply before showing a reused overlay, so a cloud recording cannot flash its old text window.
            _liveOverlay.SetTranscriptPreviewEnabled(showTranscript);
            _liveOverlay.SetLayout(OverlayPreferences);
            _liveOverlay.DisplayProvider = ResolveOverlayDisplayArea;
            _liveOverlay.SetMode(_overlayMode, ResolveOverlayDisplayArea());
            _liveOverlay.ActivateWithoutTakingFocus();
            _liveOverlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
            if (_dictation.OverlayState.Phase is DictationPhase.Error or DictationPhase.Copied) _ = HideErrorOverlayAsync(revision);
        }
        else
        {
            _liveOverlay?.HidePreview();
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
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private Task _closedSettingsImports = Task.CompletedTask;
    private Task ShutdownSettingsImportsAsync() => Task.WhenAll(_closedSettingsImports,
        _settingsWindow?.ShutdownSetupImportAsync() ?? Task.CompletedTask);
    private bool _technicalDetailsEnabled;
    private bool _transcriptPreviewEnabled = true;
    private OverlayMode _overlayMode = OverlayMode.Standard;
    private FileTranscriptionView? _fileTranscription;
    private LexiconView? _lexicon;

    internal void ShowOutputReview(TypeWhisper.Presentation.DictationOutputResult result)
    {
        if (_closing || _profileRestoreClosing || _reviewAdmissionClosed) return;
        var review = new DictationReviewWindow(result, _dictation.PluginRuntime);
        _reviewWindows.Add(review);
        review.Closed += (_, _) => _reviewWindows.Remove(review);
        review.ShowInFront();
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
        LoadOverlayPreferences();
        RemoveRetiredQuickLaunchFiles();
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
        _historyService = historyService;
        WinUICloudSync.History = historyService;
        _dictation = new LocalDictationSession(historyService, WinRT.Interop.WindowNative.GetWindowHandle(this));
        _httpApi = new WinUIHttpApi(_dictation, DispatcherQueue);
        _httpApi.DataChanged += () =>
        {
            _lexicon?.RefreshApiData();
            WorkflowsView.RefreshApiData();
            if (_workflowShortcuts?.Initialize() is { } error) ShowNotice(new AppNotice(error));
        };
        _dictation.StopHistoryPlayback = () => { _historyWindow?.StopAudioPlayback(); RecorderView.StopAudioPlayback(); };
        RecorderView.CanPlayAudio = CanPlayHistoryAudio;
        RecorderView.PrepareAudioPlayback = async () =>
        {
            _historyWindow?.StopAudioPlayback();
            await _dictation.SpokenFeedback.CancelAndDrainAsync();
        };
        WorkflowsView.Connect(_dictation);
        InitializeSettingsPages();
        _dictation.ReviewRequested += ShowOutputReview;
        _dictation.OutputWarning += message => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) ShowNotice(new AppNotice(message, "Dictation delivered with a warning"));
        });
        _dictation.EngineNotice += message => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closing) ShowNotice(new AppNotice(message, "Speech engine"));
        });
        _dictation.OutputCompleted += id => DispatcherQueue.TryEnqueue(() => _ = HideCompletedOverlayAsync(id));
        historyService.RecordsChanged += () => DispatcherQueue.TryEnqueue(async () =>
        {
            if (_historyWindow is not null) await _historyWindow.RefreshAsync();
            await RefreshSettingsPagesAsync();
        });
        PluginsView.ConfigureRuntime(_dictation);
        _dictation.Changed += () => DispatcherQueue.TryEnqueue(UpdateLiveDictation);
        RecorderView.Connect(_dictation);
        _httpApi.RecorderRequest = RecorderView.HandleApiAsync;
        _httpApi.ImportSettings = (store, preview) => RestoreApiProfile?.Invoke(store, preview) ?? Task.CompletedTask;
        RecorderView.IsQueuedSource = path => _fileTranscription?.ContainsSource(path) == true;
        RecorderView.TranscribeRequested += path => OpenFileTranscription(() => _fileTranscription?.AddRecording(path));
        MarketplaceView.ConfigureRuntime(_dictation);
        PluginsView.UseSettingsLayout();
        PluginsView.MarketplaceRequested += (_, _) => SwitchIntegrationTab(discover: true);
        MarketplaceView.ManageRequested += id => OpenProviderSettings(id);
        MarketplaceView.RestartRequested = RestartForPluginUpdateAsync;
        PluginsView.RestartRequested = RestartForPluginUpdateAsync;
        InitializeIntegrationSettings();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            AppWindow.Hide();
        };
    }

    private DisplayArea ResolveOverlayDisplayArea()
    {
        if (OverlayPreferences.Screen == OverlayScreen.PrimaryScreen) return DisplayArea.Primary;
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            var area = DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(foreground), DisplayAreaFallback.None);
            if (area is not null) return area;
        }
        if (GetCursorPos(out var cursor))
            return DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
        return DisplayArea.Primary;
    }

    // Settings and History open on the display of the app in front, or under the pointer.
    internal DisplayArea ResolveInvocationDisplayArea()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero)
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

        return DisplayArea.Primary;
    }

    private void ShowWaveformOverlay()
    {
        try
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(_transcriptPreviewEnabled);
                _overlay.FloatingPlacementChanged += frame => _settingsWindow?.SetFloatingPlacement(frame);
                _overlay.Closed += (_, _) => _overlay = null;
            }
            var area = ResolveOverlayDisplayArea();
            _overlay.DisplayProvider = ResolveOverlayDisplayArea;
            _overlay.SetLayout(OverlayPreferences);
            _overlay.SetMode(_overlayMode, area);
            _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
            _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
            _overlay.ActivateWithoutTakingFocus();
            UpdateOverlayControls();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _overlay = null;
            ShowNotice(new AppNotice($"The overlay preview could not open: {exception.Message}"));
        }
    }

    // Quick Launch was retired; its pins, usage, window and shortcut files are no longer read.
    private static void RemoveRetiredQuickLaunchFiles()
    {
        foreach (var name in new[] { "quick-launch-hotkeys.txt", "quick-launch-pins.json", "quick-launch-usage.json", "quick-launch-shortcuts.json", "quick-launch-window.json" })
        {
            try { File.Delete(WinUIProfile.DataPath(name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("Could not remove {0}: {1}", name, ex.Message); }
        }
    }

    private OverlayPreferences _layoutPreferences = new(OverlayMode.Standard, true, false);
    private readonly Dictionary<string, string> _settingsValues = new();
    private OverlayPreferences OverlayPreferences => _layoutPreferences with { Mode = _overlayMode, LiveText = _transcriptPreviewEnabled, TechnicalDetails = _technicalDetailsEnabled };
    private static readonly string OverlayPreferencesPath = WinUIProfile.DataPath("overlay.json");
    private string? _overlayPreferencesError;
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
        { _overlayPreferencesError = "Could not load overlay preferences: " + ex.Message; }
    }
    private bool SaveOverlayPreferences(OverlayPreferences? preferences = null)
    {
        try
        {
            OverlayPreferencesStore.Save(OverlayPreferencesPath, preferences ?? OverlayPreferences);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _overlayPreferencesError = "Could not save overlay preferences: " + ex.Message; return false; }
    }

    internal void OpenSetup(bool returnToTray = false)
    {
        OpenSettings();
        _settingsWindow?.ShowSetup(returnToTray);
    }

    internal void OpenSettings()
    {
        if (_profileRestoreClosing) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(OverlayPreferences, _settingsValues);
            _settingsWindow.SetIntegrationsContent(_integrationSettingsHost!);
            _settingsWindow.NavigateIntegrationBack = NavigateIntegrationSettingsBack;
            _settingsWindow.CanLeaveIntegrationAsync = PluginsView.CanLeaveSettingsAsync;
            _settingsWindow.IntegrationRequested += ShowIntegrationPage;
            _settingsWindow.IntegrationDismissed += PluginsView.CloseSettingsPage;
            _settingsWindow.UpdateIntegrationNavigation(PluginsView.SettingsNavigationItems);
            _settingsWindow.SetLiveTranscriptionAvailability(_dictation.SupportsLiveTranscription);
            _settingsWindow.CommitRecentTranscriptionsHotkeys = ChangeHistoryShortcut;
            _settingsWindow.CommitCopyLastTranscriptionHotkeys = ChangeCopyLastShortcut;
            _settingsWindow.CommitPasteLastTranscriptionHotkeys = ChangePasteLastShortcut;
            _settingsWindow.CommitReadLastTranscriptionHotkeys = ChangeReadLastShortcut;
            _settingsWindow.CommitWorkflowPaletteHotkeys = ChangeWorkflowPaletteShortcut;
            _settingsWindow.CommitRecordingShortcut = ChangeRecordingShortcut;
            _settingsWindow.CommitRecorderHotkeys = ChangeRecorderShortcut;
            _settingsWindow.CommitDictationHotkeys = ChangeDictationHotkeys;
            _settingsWindow.CommitCancelProcessingHotkeys = value =>
            {
                if (_closing || _profileRestoreClosing) return "The app is shutting down.";
                if (_cancelProcessingHotkey is null) return "Cancel shortcuts are unavailable. Wait for startup to finish or restart the app.";
                if (_workflowShortcuts?.Conflict(value) is { } conflict) return conflict;
                if (RecordingShortcutConflict(value) is { } recordingConflict) return recordingConflict;
                if (RecorderShortcutConflict(value) is { } recorderConflict) return recorderConflict;
                if (WorkflowPaletteShortcutConflict(value) is { } paletteConflict) return paletteConflict;
                if (HistoryShortcutConflict(value) is { } historyConflict) return historyConflict;
                if (CopyLastShortcutConflict(value) is { } copyConflict) return copyConflict;
                if (PasteLastShortcutConflict(value) is { } pasteConflict) return pasteConflict;
                if (ReadLastShortcutConflict(value) is { } readConflict) return readConflict;
                var error = _cancelProcessingHotkey.TryChange(value);
                _settingsValues["CancelProcessingHotkeys"] = _cancelProcessingHotkey.Value;
                return error;
            };
            _settingsWindow.CreateSetupWizard = exit => new SetupWizard(_settingsValues, exit,
                value => _closing || _profileRestoreClosing ? "The app is shutting down." : ChangeDictationHotkeys(value),
                _dictation);
            var dictationSettings = new LiveDictationSettings(_dictation, OpenProviderSettings);
            var startup = WindowsStartupRegistration.Create();
            DictationRecoveryView? recoveryView = null;
            _settingsWindow.ConfigureLiveSettings = (category, content, pickers) =>
            {
                dictationSettings.Configure(category, content, pickers);
                LiveStartupSettings.Configure(category, content, pickers, startup);
                LiveApplicationUpdateSettings.Configure(category, content, pickers, ApplicationUpdates);
                if (category == "Advanced")
                {
                    content.Children.Clear(); pickers.Clear();
                    content.Children.Add(new TextBlock { Text = "Advanced", FontSize = 22, Margin = new Thickness(0, 0, 0, 12) });
                    content.Children.Add(new HttpApiSettingsView(_httpApi));
                    Border SectionDivider() => new() { Height = 1, Margin = new Thickness(0, 12, 0, 12),
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["HairlineBrush"] };
                    content.Children.Add(SectionDivider());
                    content.Children.Add(new CliSettingsView());
                    content.Children.Add(SectionDivider());
                    content.Children.Add(new DiagnosticsSettingsView());
                    content.Children.Add(SectionDivider());
                    content.Children.Add(new TextBlock { Text = "Integrations", FontSize = 18 });
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
                if (category == "Privacy") content.Children.Insert(Math.Min(1, content.Children.Count), HistoryWorkspaceSection());
            };
            _settingsWindow.WorkspaceChanged += SettingsPageChanged;
            _settingsWindow.WorkspaceBack = SettingsPageBack;
            _settingsWindow.WorkspaceKey = SettingsPageKey;
            _settingsWindow.WorkspacePage = SettingsPage;
            _settingsWindow.Closed += (_, _) =>
            {
                // Preserve direct-close drains after the window reference is cleared.
                _closedSettingsImports = ShutdownSettingsImportsAsync();
                _settingsWindow?.DetachIntegrationsContent();
                CloseRecoveryView(recoveryView);
                _settingsWindow = null;
                // Closing the preview controls must release the demo microphone source.
                // The recording overlay is separate and continues to follow dictation.
                if (_overlay?.IsPreviewVisible == true) EndPreview_Click(this, new RoutedEventArgs());
            };
            _settingsWindow.PreferencesChanged += preferences =>
            {
                if (!SaveOverlayPreferences(preferences))
                {
                    _settingsWindow.SetPreferences(OverlayPreferences);
                    _settingsWindow.ShowOverlaySaveError(_overlayPreferencesError ?? "Could not save overlay preferences.");
                    return;
                }
                var modeChanged = _overlayMode != preferences.Mode || _layoutPreferences.Screen != preferences.Screen || _layoutPreferences.Anchor != preferences.Anchor
                    || _layoutPreferences.Left != preferences.Left || _layoutPreferences.Right != preferences.Right;
                _layoutPreferences = preferences;
                _overlayMode = preferences.Mode;
                _transcriptPreviewEnabled = preferences.LiveText;
                _technicalDetailsEnabled = preferences.TechnicalDetails;
                if (_liveOverlay?.IsPreviewVisible == true) UpdateLiveDictation();
                if (_overlay?.IsPreviewVisible == true)
                {
                    // Position changes use the selected display and anchor.
                    // A text-only toggle must not resize or move its lower block.
                    _overlay.SetLayout(preferences);
                    if (modeChanged)
                        _overlay.SetMode(_overlayMode, ResolveOverlayDisplayArea());
                    _overlay.SetTranscriptPreviewEnabled(_transcriptPreviewEnabled);
                    _overlay.SetTechnicalDetailsEnabled(_technicalDetailsEnabled);
                }
                UpdateOverlayControls();
            };
            _settingsWindow.PreviewSizeRequested += (width, height) => _overlay?.SetFloatingTextSize(width, height);
            _settingsWindow.PreviewDismissed += HideOverlayPreview;
            _settingsWindow.PausePreviewRequested += PausePreview_Click;
            _settingsWindow.PreviewRequested += (_, _) =>
            {
                if (_overlay?.IsPreviewVisible == true) EndPreview_Click(this, new RoutedEventArgs());
                else ShowWaveformOverlay();
            };
        }
        _settingsWindow.SetFloatingPlacement(_overlay?.FloatingPlacement);
        _settingsWindow.SetPreferences(OverlayPreferences);
        _settingsWindow.SetPreviewVisible(_overlay?.IsPreviewVisible == true, _overlay?.IsPaused == true);
        _settingsWindow.ShowOn(ResolveInvocationDisplayArea());
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

    private void SwitchIntegrationTab(bool discover) => ShowIntegrationSettings(discover);

    private void OpenProviderSettings(string pluginId)
    {
        OpenSettings();
        _settingsWindow?.ShowCategory("plugin:" + pluginId);
    }



    private void PausePreview_Click(object? sender, EventArgs e)
    {
        _overlay?.TogglePaused();
        UpdateOverlayControls();
    }

    private void HideOverlayPreview()
    {
        _overlay?.HidePreview();
        _settingsWindow?.SetPreviewVisible(false);
    }

    private void EndPreview_Click(object sender, RoutedEventArgs e)
    {
        HideOverlayPreview();
        UpdateOverlayControls();
    }

    private void UpdateOverlayControls()
    {
        _settingsWindow?.SetPreferences(OverlayPreferences);
        _settingsWindow?.SetPreviewVisible(_overlay?.IsPreviewVisible == true, _overlay?.IsPaused == true);
        _settingsWindow?.SetLiveTranscriptionAvailability(_dictation.SupportsLiveTranscription);
    }


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
