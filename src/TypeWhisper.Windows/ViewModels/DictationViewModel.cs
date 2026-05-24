using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SherpaOnnx;
using TypeWhisper.Core;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

public sealed record ApiDictationTranscription(
    string Text,
    string RawText,
    DateTime Timestamp,
    string? AppName,
    string? AppProcessName,
    string? AppUrl,
    double Duration,
    string? Language,
    string Engine,
    string? Model,
    int WordsCount);

public sealed record ApiDictationSessionSnapshot(
    Guid Id,
    ApiDictationSessionStatus Status,
    ApiDictationTranscription? Transcription,
    string? Error);

public enum ApiDictationSessionStatus
{
    Recording,
    Processing,
    Completed,
    Failed
}

public partial class DictationViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly ModelManagerService _modelManager;
    private readonly AudioRecordingService _audio;
    private readonly HotkeyService _hotkey;
    private readonly TextInsertionService _textInsertion;
    private readonly IActiveWindowService _activeWindow;
    private readonly SoundService _sound;
    private readonly IHistoryService _history;
    private readonly IDictionaryService _dictionary;
    private readonly IVocabularyBoostingService _vocabularyBoosting;
    private readonly ISnippetService _snippets;
    private readonly IWorkflowService _workflows;
    private readonly ITranslationService _translation;
    private readonly IAudioDuckingService _audioDucking;
    private readonly IMediaPauseService _mediaPause;
    private readonly PluginEventBus _eventBus;
    private readonly IWorkflowTextProcessor _workflowTextProcessor;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly IErrorLogService _errorLog;
    private readonly SpeechFeedbackService _speechFeedback;
    private readonly RecentTranscriptionsService _recentTranscriptions;
    private readonly WorkflowPaletteService _workflowPalette;

    private CancellationTokenSource _consumerCts = new();
    private Task? _consumerTask;
    private System.Timers.Timer? _durationTimer;
    private bool _isRecording;
    private bool _isStoppingRecording;
    private int _pendingJobCount;
    private const int MaxTrackedApiDictationSessions = 50;
    private const string ExternalLiveTranscriptPluginId = "com.typewhisper.live-transcript";
    private readonly object _apiSessionLock = new();
    private readonly Dictionary<Guid, ApiDictationSessionSnapshot> _apiDictationSessions = [];
    private readonly List<Guid> _apiDictationSessionOrder = [];
    private Guid? _activeApiDictationSessionId;
    // Identifies the current recording session; stamped on all related events.
    private Guid? _currentRecordingId;

    private readonly Channel<TranscriptionJob> _jobChannel =
        Channel.CreateBounded<TranscriptionJob>(new BoundedChannelOptions(5)
        { FullMode = BoundedChannelFullMode.Wait });

    // Captured at recording start for the current session
    private Workflow? _activeWorkflow;
    private string? _workflowHotkeyOverrideId;
    private IntPtr _capturedWindowHandle;
    private string? _capturedProcessName;
    private string? _capturedWindowTitle;
    private string? _capturedUrl;

    // Live transcription
    private readonly StreamingHandler _streamingHandler;
    // VAD for live transcription (fallback for non-plugin models)
    private VoiceActivityDetector? _vad;
    private readonly List<string> _partialSegments = [];
    private readonly SemaphoreSlim _vadLock = new(1, 1);
    private bool _disposed;
    private int _lastVadFlushedSegmentCount;
    private int _lastVadDiscardedShortSegmentCount;
    private int _lastVadSegmentLength;

    [ObservableProperty] private DictationState _state = DictationState.Idle;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private string _statusText = Loc.Instance["Status.Ready"];
    [ObservableProperty] private string _transcribedText = "";
    [ObservableProperty] private HotkeyMode? _currentHotkeyMode;
    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private string? _activeProcessName;
    [ObservableProperty] private string? _activeWorkflowName;
    [ObservableProperty] private string _partialText = "";
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string? _feedbackText;
    [ObservableProperty] private bool _feedbackIsError;
    [ObservableProperty] private bool _showFeedback;
    [ObservableProperty] private string? _lastTranscribedText;
    [ObservableProperty] private string? _lastTranscriptionLanguage;

    public bool CanReadBackLastTranscription => !string.IsNullOrWhiteSpace(LastTranscribedText);

    public DictationViewModel(
        ISettingsService settings,
        ModelManagerService modelManager,
        AudioRecordingService audio,
        HotkeyService hotkey,
        TextInsertionService textInsertion,
        IActiveWindowService activeWindow,
        SoundService sound,
        IHistoryService history,
        IDictionaryService dictionary,
        IVocabularyBoostingService vocabularyBoosting,
        ISnippetService snippets,
        IWorkflowService workflows,
        ITranslationService translation,
        IAudioDuckingService audioDucking,
        IMediaPauseService mediaPause,
        IWorkflowTextProcessor workflowTextProcessor,
        IPostProcessingPipeline pipeline,
        IErrorLogService errorLog,
        SpeechFeedbackService speechFeedback,
        RecentTranscriptionsService recentTranscriptions,
        WorkflowPaletteService workflowPalette)
    {
        _settings = settings;
        _modelManager = modelManager;
        _audio = audio;
        _hotkey = hotkey;
        _textInsertion = textInsertion;
        _activeWindow = activeWindow;
        _sound = sound;
        _history = history;
        _dictionary = dictionary;
        _vocabularyBoosting = vocabularyBoosting;
        _snippets = snippets;
        _workflows = workflows;
        _translation = translation;
        _audioDucking = audioDucking;
        _mediaPause = mediaPause;
        _eventBus = modelManager.PluginManager.EventBus;
        _workflowTextProcessor = workflowTextProcessor;
        _pipeline = pipeline;
        _errorLog = errorLog;
        _speechFeedback = speechFeedback;
        _recentTranscriptions = recentTranscriptions;
        _workflowPalette = workflowPalette;

        _streamingHandler = new StreamingHandler(modelManager, audio, dictionary);
        _streamingHandler.OnPartialTextUpdate = text =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() => PartialText = text);
            _eventBus.Publish(new PartialTranscriptionUpdateEvent { PartialText = text, RecordingId = _currentRecordingId });
        };

        _consumerTask = Task.Run(() => ProcessJobsAsync(_consumerCts.Token));

        _audio.AudioLevelChanged += OnAudioLevelChanged;
        _audio.DeviceLost += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (_isRecording)
            {
                _isRecording = false;
                _audio.StopRecording();
                StopActiveRecordingInfrastructure();
            }

            ApplyTransientIdleFeedback(Loc.Instance["Status.NoMicrophone"], feedbackIsError: true);
        });
        _audio.DeviceAvailable += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ShowTransientFeedback(Loc.Instance["Status.MicrophoneRestored"], isError: false);
        });
        _settings.SettingsChanged += _ =>
        {
            OnPropertyChanged(nameof(LeftWidget));
            OnPropertyChanged(nameof(RightWidget));
            OnPropertyChanged(nameof(IndicatorStyle));
            OnPropertyChanged(nameof(OverlayPosition));
            OnPropertyChanged(nameof(LiveTranscriptionEnabled));
            OnPropertyChanged(nameof(LiveTranscriptionFontSize));
            RefreshPartialPreviewPresentation();
        };
        _hotkey.DictationStartRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () => await StartRecording());
        _hotkey.DictationStopRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await StopRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StopRecording error: {ex}");
                _isRecording = false;
                StopActiveRecordingInfrastructure();
                ApplyTransientIdleFeedback(
                    Loc.Instance.GetString("Status.ErrorFormat", ex.Message),
                    feedbackIsError: true);
            }
        });

        _hotkey.CancelRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
            await AbortActiveOperation());
        _hotkey.RecentTranscriptionsRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(ShowRecentTranscriptionsPalette);
        _hotkey.CopyLastTranscriptionRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
            await CopyLastTranscriptionToClipboardAsync());
        _hotkey.WorkflowPaletteRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
            await ShowWorkflowPaletteAsync());
        _hotkey.WorkflowDictationRequested += (_, workflowId) => Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            _workflowHotkeyOverrideId = workflowId;
            await StartRecording();
        });
        _hotkey.WorkflowTextProcessingRequested += (_, workflowId) => Application.Current?.Dispatcher.InvokeAsync(async () =>
            await ProcessWorkflowHotkeyTextAsync(workflowId));
        _recentTranscriptions.FeedbackRequested += (message, isError) =>
            Application.Current?.Dispatcher.InvokeAsync(() => ShowRecentTranscriptionFeedback(message, isError));
        _workflowPalette.FeedbackRequested += (message, isError) =>
            Application.Current?.Dispatcher.InvokeAsync(() => ShowRecentTranscriptionFeedback(message, isError));
        _modelManager.PluginManager.PluginStateChanged += OnPluginStateChanged;
    }

    public OverlayWidget LeftWidget => _settings.Current.OverlayLeftWidget;
    public OverlayWidget RightWidget => _settings.Current.OverlayRightWidget;
    public IndicatorStyle IndicatorStyle => _settings.Current.IndicatorStyle;
    public OverlayPosition OverlayPosition => _settings.Current.OverlayPosition;
    public bool LiveTranscriptionEnabled => _settings.Current.LiveTranscriptionEnabled;
    public double LiveTranscriptionFontSize =>
        AppSettings.NormalizeLiveTranscriptionFontSize(_settings.Current.LiveTranscriptionFontSize);
    public bool ShowInlineFeedback =>
        DictationOverlayPresentation.ShowInlineFeedback(IsOverlayVisible, ShowFeedback);
    public bool ShowDetachedFeedback =>
        DictationOverlayPresentation.ShowDetachedFeedback(IsOverlayVisible, ShowFeedback);
    public bool HasOverlayContentVisible =>
        DictationOverlayPresentation.HasVisibleContent(IsOverlayVisible, ShowFeedback);
    public bool ExternalLivePreviewActive =>
        _modelManager.PluginManager.IsEnabled(ExternalLiveTranscriptPluginId);
    public bool ShowBuiltInPartialPreview =>
        DictationOverlayPresentation.ShowBuiltInPartialPreview(
            PartialText,
            ExternalLivePreviewActive,
            LiveTranscriptionEnabled,
            IndicatorStyle);

    partial void OnPartialTextChanged(string value)
    {
        RefreshPartialPreviewPresentation();
    }

    partial void OnCurrentHotkeyModeChanged(HotkeyMode? value)
    {
        if (_isRecording)
            StatusText = GetRecordingStatusText();
    }

    private string GetRecordingStatusText()
    {
        return CurrentHotkeyMode switch
        {
            HotkeyMode.Toggle => Loc.Instance["Status.RecordingToggle"],
            HotkeyMode.PushToTalk => Loc.Instance["Status.RecordingHold"],
            _ => Loc.Instance["Status.Recording"]
        };
    }

    private System.Timers.Timer? _feedbackTimer;

    partial void OnShowFeedbackChanged(bool value)
    {
        RaiseOverlayPresentationChanged();
        _feedbackTimer?.Stop();
        _feedbackTimer?.Dispose();
        if (value)
        {
            _feedbackTimer = new System.Timers.Timer(2000);
            _feedbackTimer.AutoReset = false;
            _feedbackTimer.Elapsed += (_, _) =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() => ShowFeedback = false);
            };
            _feedbackTimer.Start();
        }
    }

    partial void OnLastTranscribedTextChanged(string? value) =>
        OnPropertyChanged(nameof(CanReadBackLastTranscription));

    partial void OnIsOverlayVisibleChanged(bool value) => RaiseOverlayPresentationChanged();

    private void OnPluginStateChanged(object? sender, EventArgs e)
    {
        if (Application.Current is { } app)
        {
            app.Dispatcher.InvokeAsync(RefreshPartialPreviewPresentation);
            return;
        }

        RefreshPartialPreviewPresentation();
    }

    private void RefreshPartialPreviewPresentation()
    {
        IsExpanded = ShowBuiltInPartialPreview;
        OnPropertyChanged(nameof(ExternalLivePreviewActive));
        OnPropertyChanged(nameof(ShowBuiltInPartialPreview));
    }

    public void ReadBackLastTranscription()
    {
        if (string.IsNullOrWhiteSpace(LastTranscribedText))
            return;

        _speechFeedback.ReadBack(LastTranscribedText, LastTranscriptionLanguage);
    }

    public void ShowRecentTranscriptionsPalette()
    {
        if (State != DictationState.Idle)
            return;

        _recentTranscriptions.TogglePalette();
    }

    public Task CopyLastTranscriptionToClipboardAsync() =>
        _recentTranscriptions.CopyLastTranscriptionToClipboardAsync();

    public async Task ShowWorkflowPaletteAsync()
    {
        if (State != DictationState.Idle)
            return;

        await _workflowPalette.TogglePaletteAsync();
    }

    public async Task ProcessWorkflowHotkeyTextAsync(string workflowId)
    {
        if (State != DictationState.Idle)
            return;

        var workflow = _workflows.GetWorkflow(workflowId);
        if (workflow is null)
            return;

        await _workflowPalette.ExecuteWorkflowAsync(workflow);
    }

    // Effective settings: workflow override -> global setting
    private string? EffectiveLanguage =>
        _activeWorkflow?.Behavior.InputLanguage ?? _settings.Current.Language;

    private TranscriptionTask EffectiveTask =>
        (_activeWorkflow?.Behavior.SelectedTask ?? _settings.Current.TranscriptionTask) == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;

    private bool EffectiveWhisperMode =>
        _activeWorkflow?.Behavior.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;

    private string? EffectiveModelId =>
        _activeWorkflow?.Behavior.TranscriptionModelOverride;

    [RelayCommand]
    /// <summary>Public API for starting recording (used by HTTP API).</summary>
    public Task StartRecordingAsync() => StartRecording();

    /// <summary>Public API for stopping recording (used by HTTP API).</summary>
    public Task StopRecordingAsync() => StopRecording();

    /// <summary>Whether the service is currently recording.</summary>
    public bool IsRecording => _isRecording;

    public async Task<Guid> StartRecordingForApiAsync()
    {
        var sessionId = Guid.NewGuid();
        _activeApiDictationSessionId = sessionId;
        StoreApiDictationSession(new ApiDictationSessionSnapshot(
            sessionId,
            ApiDictationSessionStatus.Recording,
            null,
            null));

        await StartRecording();

        if (!_isRecording)
            FailApiDictationSession(sessionId, StatusText);

        return sessionId;
    }

    public async Task<Guid?> StopRecordingForApiAsync()
    {
        var sessionId = _activeApiDictationSessionId;
        if (sessionId is not null)
            MarkApiDictationSessionProcessing(sessionId.Value);

        await StopRecording();
        return sessionId;
    }

    public ApiDictationSessionSnapshot? GetApiDictationSession(Guid id)
    {
        lock (_apiSessionLock)
        {
            if (_apiDictationSessions.TryGetValue(id, out var session))
                return session;
        }

        var record = _history.Records.FirstOrDefault(r =>
            Guid.TryParse(r.Id, out var recordId) && recordId == id);
        if (record is null)
            return null;

        return new ApiDictationSessionSnapshot(
            id,
            ApiDictationSessionStatus.Completed,
            new ApiDictationTranscription(
                record.FinalText,
                record.RawText,
                record.Timestamp,
                record.AppName,
                record.AppProcessName,
                record.AppUrl,
                record.DurationSeconds,
                record.Language,
                record.EngineUsed,
                record.ModelUsed,
                record.WordCount),
            null);
    }

    private void RaiseOverlayPresentationChanged()
    {
        OnPropertyChanged(nameof(ShowInlineFeedback));
        OnPropertyChanged(nameof(ShowDetachedFeedback));
        OnPropertyChanged(nameof(HasOverlayContentVisible));
    }

    private void ClearCapturedContext()
    {
        ActiveProcessName = null;
        ActiveWorkflowName = null;
        _activeWorkflow = null;
        _capturedWindowHandle = IntPtr.Zero;
        _capturedProcessName = null;
        _capturedWindowTitle = null;
        _capturedUrl = null;
    }

    private void ClearPartialPreview()
    {
        _partialSegments.Clear();
        PartialText = "";
        IsExpanded = false;
    }

    private int DecrementPendingJobCount()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingJobCount);
            if (current == 0)
                return 0;

            var next = current - 1;
            if (Interlocked.CompareExchange(ref _pendingJobCount, next, current) == current)
                return next;
        }
    }

    internal static bool ShouldEnableCancelShortcut(bool isRecording, int pendingJobCount) =>
        isRecording || pendingJobCount > 0;

    private void StoreApiDictationSession(ApiDictationSessionSnapshot session)
    {
        lock (_apiSessionLock)
        {
            _apiDictationSessions[session.Id] = session;
            _apiDictationSessionOrder.Remove(session.Id);
            _apiDictationSessionOrder.Add(session.Id);

            while (_apiDictationSessionOrder.Count > MaxTrackedApiDictationSessions)
            {
                var removedId = _apiDictationSessionOrder[0];
                _apiDictationSessionOrder.RemoveAt(0);
                _apiDictationSessions.Remove(removedId);
            }
        }
    }

    private void MarkApiDictationSessionProcessing(Guid sessionId)
    {
        StoreApiDictationSession(new ApiDictationSessionSnapshot(
            sessionId,
            ApiDictationSessionStatus.Processing,
            null,
            null));
    }

    private void CompleteApiDictationSession(Guid? sessionId, ApiDictationTranscription transcription)
    {
        if (sessionId is null)
            return;

        StoreApiDictationSession(new ApiDictationSessionSnapshot(
            sessionId.Value,
            ApiDictationSessionStatus.Completed,
            transcription,
            null));

        if (_activeApiDictationSessionId == sessionId)
            _activeApiDictationSessionId = null;
    }

    private void FailApiDictationSession(Guid? sessionId, string error)
    {
        if (sessionId is null)
            return;

        StoreApiDictationSession(new ApiDictationSessionSnapshot(
            sessionId.Value,
            ApiDictationSessionStatus.Failed,
            null,
            error));

        if (_activeApiDictationSessionId == sessionId)
            _activeApiDictationSessionId = null;
    }

    private void PublishNoSpeechFailure(string? modelId, string? appName, Guid? recordingId)
    {
        _eventBus.Publish(new TranscriptionFailedEvent
        {
            ErrorMessage = Loc.Instance["Status.NoSpeech"],
            ModelId = modelId,
            AppName = appName,
            RecordingId = recordingId
        });
    }

    private void ShowTransientFeedback(string text, bool isError)
    {
        FeedbackText = text;
        FeedbackIsError = isError;

        if (ShowFeedback)
            ShowFeedback = false;

        ShowFeedback = true;
    }

    private void ShowRecentTranscriptionFeedback(string text, bool isError)
    {
        if (_isRecording || _pendingJobCount > 0)
        {
            ShowTransientFeedback(text, isError);
            return;
        }

        ApplyTransientIdleFeedback(text, isError);
    }

    private void ResetSessionToIdle(bool clearFeedback = false, bool forceHotkeyStop = false)
    {
        State = DictationState.Idle;
        StatusText = Loc.Instance["Status.Ready"];
        IsOverlayVisible = false;
        RecordingSeconds = 0;
        CurrentHotkeyMode = null;
        ClearCapturedContext();
        ClearPartialPreview();

        if (clearFeedback)
        {
            FeedbackText = null;
            FeedbackIsError = false;
            ShowFeedback = false;
        }

        if (forceHotkeyStop)
            _hotkey.ForceStop();

        _hotkey.IsCancelShortcutEnabled = ShouldEnableCancelShortcut(_isRecording, _pendingJobCount);
    }

    private void ApplyTransientIdleFeedback(string feedbackText, bool feedbackIsError = false)
    {
        var resetOutcome = DictationOverlayPresentation.CreateTransientIdleFeedback(feedbackIsError);

        State = resetOutcome.State;
        IsOverlayVisible = resetOutcome.IsOverlayVisible;
        RecordingSeconds = 0;
        CurrentHotkeyMode = null;
        ClearCapturedContext();
        ClearPartialPreview();
        StatusText = Loc.Instance["Status.Ready"];
        ShowTransientFeedback(feedbackText, resetOutcome.FeedbackIsError);

        if (resetOutcome.ForceHotkeyStop)
            _hotkey.ForceStop();

        _hotkey.IsCancelShortcutEnabled = ShouldEnableCancelShortcut(_isRecording, _pendingJobCount);
    }

    private void StopActiveRecordingInfrastructure()
    {
        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;

        _streamingHandler.Stop();
        _audio.SamplesAvailable -= OnSamplesAvailable;
        _audioDucking.RestoreAudio();
        _mediaPause.ResumeMedia();
        _vad?.Dispose();
        _vad = null;
        RecordingSeconds = 0;
        CurrentHotkeyMode = null;
    }

    private void ApplyModelLoadFailureFeedback(Exception ex)
    {
        _isRecording = false;
        ApplyTransientIdleFeedback(
            Loc.Instance.GetString("Status.ModelErrorFormat", ex.Message),
            feedbackIsError: true);
    }

    private async Task StartRecording()
    {
        if (_isRecording || _isStoppingRecording) return;
        _isRecording = true;
        FeedbackText = null;
        FeedbackIsError = false;
        ShowFeedback = false;

        // Capture active window context at recording start
        _capturedWindowHandle = _activeWindow.GetActiveWindowHandle();
        _capturedProcessName = _activeWindow.GetActiveWindowProcessName();
        _capturedWindowTitle = _activeWindow.GetActiveWindowTitle();
        _capturedUrl = _activeWindow.GetBrowserUrl();
        if (_workflowHotkeyOverrideId is not null)
        {
            _activeWorkflow = _workflows.ForceMatch(_workflowHotkeyOverrideId)?.Workflow;
            _workflowHotkeyOverrideId = null;
        }
        else
        {
            _activeWorkflow = _workflows.MatchWorkflow(_capturedProcessName, _capturedUrl)?.Workflow;
        }

        var desiredModelId = EffectiveModelId ?? _settings.Current.SelectedModelId;
        if (string.IsNullOrWhiteSpace(desiredModelId))
        {
            StatusText = Loc.Instance["Status.NoModelLoaded"];
            _isRecording = false;
            return;
        }

        try
        {
            if (!await _modelManager.EnsureModelLoadedAsync(desiredModelId))
            {
                StatusText = Loc.Instance["Status.NoModelLoaded"];
                _isRecording = false;
                return;
            }
        }
        catch (OperationCanceledException)
        {
            _isRecording = false;
            return;
        }
        catch (InvalidOperationException ex)
        {
            ApplyModelLoadFailureFeedback(ex);
            return;
        }
        catch (IOException ex)
        {
            ApplyModelLoadFailureFeedback(ex);
            return;
        }
        catch (ArgumentException ex)
        {
            ApplyModelLoadFailureFeedback(ex);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            ApplyModelLoadFailureFeedback(ex);
            return;
        }

        if (!_modelManager.Engine.IsModelLoaded)
        {
            _isRecording = false;
            ApplyTransientIdleFeedback(Loc.Instance["Status.NoModelLoaded"], feedbackIsError: true);
            return;
        }

        if (!_audio.HasDevice)
        {
            _isRecording = false;
            ApplyTransientIdleFeedback(Loc.Instance["Status.NoMicrophone"], feedbackIsError: true);
            return;
        }

        ActiveProcessName = _capturedProcessName;
        ActiveWorkflowName = _activeWorkflow?.Name;

        _audio.WhisperModeEnabled = EffectiveWhisperMode;

        // Live transcription: streaming handler polls growing buffer periodically
        _partialSegments.Clear();
        PartialText = "";
        _vad?.Dispose();
        _vad = null;
        _streamingHandler.Stop();

        var isPluginModel = _modelManager.ActiveModelId is not null
            && ModelManagerService.IsPluginModel(_modelManager.ActiveModelId);

        var liveTranscriptionMode = LiveTranscriptionStartupPolicy.Select(
            _settings.Current,
            isPluginModel,
            _modelManager.ActiveTranscriptionPlugin);

        if (liveTranscriptionMode is LiveTranscriptionStartupMode.PluginStreaming
            or LiveTranscriptionStartupMode.PluginPollingFallback)
        {
            _streamingHandler.Start(EffectiveLanguage, EffectiveTask, () => _isRecording);
        }
        else if (liveTranscriptionMode == LiveTranscriptionStartupMode.LegacyVad)
        {
            // VAD fallback for non-plugin models
            _vad = CreateVoiceActivityDetector();
            _audio.SamplesAvailable += OnSamplesAvailable;
        }

        _audio.StartRecording();
        if (!_audio.IsRecording)
        {
            _isRecording = false;
            StopActiveRecordingInfrastructure();
            ApplyTransientIdleFeedback(Loc.Instance["Status.NoMicrophone"], feedbackIsError: true);
            return;
        }

        _sound.PlayStartSound();

        if (_settings.Current.AudioDuckingEnabled)
            _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
        if (_settings.Current.PauseMediaDuringRecording)
            _mediaPause.PauseMedia();

        _currentRecordingId = Guid.NewGuid();
        _eventBus.Publish(new RecordingStartedEvent
        {
            AppName = _activeWindow.GetActiveWindowTitle(),
            AppProcessName = _activeWindow.GetActiveWindowProcessName(),
            RecordingId = _currentRecordingId
        });

        State = DictationState.Recording;
        CurrentHotkeyMode = _hotkey.CurrentMode;
        StatusText = GetRecordingStatusText();
        TranscribedText = "";
        IsOverlayVisible = true;
        RecordingSeconds = 0;
        _hotkey.IsCancelShortcutEnabled = ShouldEnableCancelShortcut(_isRecording, _pendingJobCount);

        _durationTimer?.Dispose();
        _durationTimer = new System.Timers.Timer(100);
        _durationTimer.Elapsed += (_, _) =>
        {
            RecordingSeconds = _audio.RecordingDuration.TotalSeconds;
            if (_hotkey.CurrentMode is { } mode && mode != CurrentHotkeyMode)
                CurrentHotkeyMode = mode;
        };
        _durationTimer.Start();
    }

    [RelayCommand]
    private async Task StopRecording()
    {
        if (!_isRecording || _isStoppingRecording) return;
        _isStoppingRecording = true;

        try
        {
            var stopRequestedAtUtc = DateTime.UtcNow;
            var streamingText = _streamingHandler.Stop();
            _audio.SamplesAvailable -= OnSamplesAvailable;

            var samples = await _audio.StopRecordingAsync();
            _isRecording = false;
            var audioTailSnapshot = _audio.CaptureTailSnapshot();
            var originalSamples = samples ?? [];
            var rawPeakRmsLevel = _audio.PreGainPeakRmsLevel;
            var rawDuration = originalSamples.Length / 16000.0;
            _eventBus.Publish(new RecordingStoppedEvent { DurationSeconds = rawDuration, RecordingId = _currentRecordingId });
            _durationTimer?.Stop();
            _durationTimer?.Dispose();
            _durationTimer = null;
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            RecordingSeconds = 0;
            CurrentHotkeyMode = null;

            // Flush remaining VAD segments
            _lastVadFlushedSegmentCount = 0;
            _lastVadDiscardedShortSegmentCount = 0;
            _lastVadSegmentLength = 0;
            var vadWasContendedOnStop = false;
            List<string> partialSnapshot;
            if (_vad is not null)
            {
                vadWasContendedOnStop = _vadLock.CurrentCount == 0;
                await _vadLock.WaitAsync();
                try
                {
                    _vad.Flush();
                    await ProcessVadSegments();
                    _vad.Dispose();
                    _vad = null;
                }
                finally
                {
                    _vadLock.Release();
                }
            }

            // Live text can confirm speech for short/quiet handling, but the
            // pasted transcript must come from the full captured buffer.
            var trustedLiveText = DictationFinalTextPolicy.SelectTrustedLiveText(streamingText);
            partialSnapshot = trustedLiveText is not null
                ? [trustedLiveText]
                : [.. _partialSegments];

            var aggressiveShortQuietHandling = _settings.Current.TranscribeShortQuietClipsAggressively;
            var shortSpeechDecision = DictationShortSpeechPolicy.Classify(
                rawDuration,
                rawPeakRmsLevel,
                partialSnapshot.Count > 0,
                aggressiveShortQuietHandling);

            if (shortSpeechDecision == ShortSpeechDecision.DiscardTooShort)
            {
                FailApiDictationSession(_activeApiDictationSessionId, Loc.Instance["Status.TooShort"]);
                _eventBus.Publish(new TranscriptionFailedEvent
                {
                    ErrorMessage = Loc.Instance["Status.TooShort"],
                    ModelId = _modelManager.ActiveModelId,
                    AppName = _capturedWindowTitle,
                    RecordingId = _currentRecordingId
                });
                ApplyTransientIdleFeedback(Loc.Instance["Status.TooShort"]);
                return;
            }

            if (shortSpeechDecision == ShortSpeechDecision.DiscardNoSpeech)
            {
                FailApiDictationSession(_activeApiDictationSessionId, Loc.Instance["Status.NoSpeech"]);
                PublishNoSpeechFailure(_modelManager.ActiveModelId, _capturedWindowTitle, _currentRecordingId);
                ApplyTransientIdleFeedback(Loc.Instance["Status.NoSpeech"]);
                return;
            }

            var apiSessionId = _activeApiDictationSessionId;
            if (apiSessionId is not null)
            {
                MarkApiDictationSessionProcessing(apiSessionId.Value);
                _activeApiDictationSessionId = null;
            }

            // Snapshot all context and enqueue — returns immediately
            var transcriptionSamples = DictationShortSpeechPolicy.PadSamplesForFinalTranscription(originalSamples, rawDuration);
            var job = new TranscriptionJob(
                transcriptionSamples,
                originalSamples,
                partialSnapshot,
                _activeWorkflow,
                _capturedWindowHandle,
                _capturedProcessName,
                _capturedWindowTitle,
                _capturedUrl,
                EffectiveLanguage,
                EffectiveTask,
                _modelManager.ActiveModelId,
                apiSessionId,
                aggressiveShortQuietHandling,
                new RecordingTailDiagnosticSnapshot(
                    _modelManager.ActiveModelId,
                    "local",
                    stopRequestedAtUtc,
                    rawDuration,
                    originalSamples.Length,
                    audioTailSnapshot.LastSamplesAvailableUtc,
                    audioTailSnapshot.RecentChunks,
                    _settings.Current.SilenceAutoStopEnabled,
                    _lastVadFlushedSegmentCount,
                    _lastVadDiscardedShortSegmentCount,
                    _lastVadSegmentLength,
                    vadWasContendedOnStop,
                    false,
                    0,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                _currentRecordingId);

            Interlocked.Increment(ref _pendingJobCount);
            await _jobChannel.Writer.WriteAsync(job);
            UpdateVisualState();
        }
        finally
        {
            _isRecording = false;
            _isStoppingRecording = false;
        }
    }

    private Task AbortActiveOperation()
    {
        if (_isStoppingRecording)
            return Task.CompletedTask;

        if (_isRecording)
        {
            _isRecording = false;
            _audio.StopRecording();
            StopActiveRecordingInfrastructure();
            FailApiDictationSession(_activeApiDictationSessionId, Loc.Instance["Status.Cancelled"]);
            ApplyTransientIdleFeedback(Loc.Instance["Status.Cancelled"]);
            return Task.CompletedTask;
        }

        if (_pendingJobCount > 0)
        {
            CancelProcessing();
            ApplyTransientIdleFeedback(Loc.Instance["Status.Cancelled"]);
        }

        return Task.CompletedTask;
    }

    private async Task ProcessJobsAsync(CancellationToken ct)
    {
        await foreach (var job in _jobChannel.Reader.ReadAllAsync(ct))
        {
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateVisualState());
            try
            {
                await ProcessSingleJobAsync(job, ct);
            }
            finally
            {
                DecrementPendingJobCount();
                await Application.Current.Dispatcher.InvokeAsync(() => UpdateVisualState());
            }
        }
    }

    private async Task ProcessSingleJobAsync(TranscriptionJob job, CancellationToken ct)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                State = DictationState.Processing;
                StatusText = Loc.Instance["Status.Processing"];
            });

            string rawText = "";
            string? detectedLanguage = null;
            double audioDuration = job.OriginalSamples.Length / 16000.0;
            var diagnosticsEnabled = _settings.Current.InternalParakeetTailDiagnosticsEnabled;
            var tailHardeningEnabled = _settings.Current.InternalParakeetTailHardeningEnabled;
            var isParakeetJob = ParakeetTailHelper.IsParakeetModel(job.ActiveModelIdAtCapture);
            var decodeSamples = isParakeetJob && tailHardeningEnabled
                ? ParakeetTailHelper.AppendTailGuard(job.Samples)
                : job.Samples;
            var tailGuardApplied = !ReferenceEquals(decodeSamples, job.Samples);
            var tailSamplesAdded = decodeSamples.Length - job.Samples.Length;
            string fullDecodeText = "";
            string? fullDecodeDetectedLanguage = null;
            long? fullDecodeDurationMs = null;

            var previewText = DictationFinalTextPolicy.JoinPreviewSegments(job.PartialSegments);
            var language = job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage;
            var decodeStartedAt = DateTime.UtcNow;
            TranscriptionResult? result = null;
            try
            {
                result = await _modelManager.Engine.TranscribeAsync(
                    decodeSamples, language, job.EffectiveTask, ct);
                fullDecodeText = result.Text ?? "";
                fullDecodeDetectedLanguage = result.DetectedLanguage;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!string.IsNullOrWhiteSpace(previewText))
            {
                rawText = DictationFinalTextPolicy.SelectRawText(null, previewText);
                detectedLanguage = language;
                _errorLog.AddEntry(
                    $"Final transcription failed; using live preview fallback: {ex.Message}",
                    ErrorCategory.Transcription);
                job = job with
                {
                    Diagnostic = job.Diagnostic with
                    {
                        TailGuardApplied = tailGuardApplied,
                        TailSamplesAdded = tailSamplesAdded,
                        FinalTextSource = "live_preview_fallback",
                        FullDecodeTextLength = 0,
                        PartialTextLength = previewText.Length
                    }
                };
            }
            finally
            {
                fullDecodeDurationMs = (long)(DateTime.UtcNow - decodeStartedAt).TotalMilliseconds;
            }

            if (result is not null && isParakeetJob)
            {
                var selection = ParakeetTailHelper.SelectResult(
                    job.ActiveModelIdAtCapture,
                    fullDecodeText,
                    job.PartialSegments);
                rawText = DictationFinalTextPolicy.SelectRawText(selection.Text, previewText);
                detectedLanguage = fullDecodeDetectedLanguage;
                var usedPreviewFallback = string.IsNullOrWhiteSpace(fullDecodeText)
                    && !string.IsNullOrWhiteSpace(previewText);
                job = job with
                {
                    Diagnostic = job.Diagnostic with
                    {
                        TailGuardApplied = tailGuardApplied,
                        TailSamplesAdded = tailSamplesAdded,
                        FinalTextSource = usedPreviewFallback ? "live_preview_fallback" : selection.Source,
                        FullDecodeTextLength = selection.FullDecodeTextLength,
                        PartialTextLength = selection.PartialTextLength,
                        DivergedFromPartials = selection.DivergedFromPartials,
                        FullDecodeDurationMs = fullDecodeDurationMs
                    }
                };
            }
            else if (result is not null)
            {
                if (DictationFinalTextPolicy.ShouldRejectAsNoSpeech(
                        result.Text,
                        result.NoSpeechProbability,
                        hasPreviewText: !string.IsNullOrWhiteSpace(previewText),
                        job.TranscribeShortQuietClipsAggressively))
                {
                    FailApiDictationSession(job.ApiSessionId, Loc.Instance["Status.NoSpeech"]);
                    PublishNoSpeechFailure(job.ActiveModelIdAtCapture, job.CapturedWindowTitle, job.RecordingId);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        ApplyTransientIdleFeedback(Loc.Instance["Status.NoSpeech"]));
                    return;
                }

                rawText = DictationFinalTextPolicy.SelectRawText(result.Text, previewText);
                detectedLanguage = result.DetectedLanguage;
                var usedPreviewFallback = string.IsNullOrWhiteSpace(
                    DictationFinalTextPolicy.SelectRawText(result.Text))
                    && !string.IsNullOrWhiteSpace(rawText);
                job = job with
                {
                    Diagnostic = job.Diagnostic with
                    {
                        FinalTextSource = usedPreviewFallback
                            ? "live_preview_fallback"
                            : string.IsNullOrWhiteSpace(fullDecodeText) ? "empty" : "full_decode",
                        FullDecodeTextLength = fullDecodeText.Length,
                        PartialTextLength = previewText.Length,
                        FullDecodeDurationMs = fullDecodeDurationMs
                    }
                };
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                FailApiDictationSession(job.ApiSessionId, Loc.Instance["Status.NoSpeech"]);
                PublishNoSpeechFailure(job.ActiveModelIdAtCapture, job.CapturedWindowTitle, job.RecordingId);
                LogParakeetTailDiagnostics(job.Diagnostic);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    ApplyTransientIdleFeedback(Loc.Instance["Status.NoSpeech"]));
                return;
            }

            if (diagnosticsEnabled && isParakeetJob)
            {
                if (!tailHardeningEnabled)
                {
                    var compareStartedAt = DateTime.UtcNow;
                    var compareResult = await _modelManager.Engine.TranscribeAsync(
                        ParakeetTailHelper.AppendTailGuard(job.Samples),
                        job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage,
                        job.EffectiveTask,
                        ct);
                    job = job with
                    {
                        Diagnostic = job.Diagnostic with
                        {
                            CompareDecodeTextLength = compareResult.Text?.Length ?? 0,
                            CompareDecodeDurationMs = (long)(DateTime.UtcNow - compareStartedAt).TotalMilliseconds
                        }
                    };
                }

                LogParakeetTailDiagnostics(job.Diagnostic);
            }

            _eventBus.Publish(new TranscriptionCompletedEvent
            {
                RawText = rawText,
                Text = rawText,
                DetectedLanguage = detectedLanguage,
                DurationSeconds = audioDuration,
                ModelId = job.ActiveModelIdAtCapture,
                ProfileName = job.ActiveWorkflow?.Name,
                AppName = job.CapturedWindowTitle,
                AppProcessName = job.CapturedProcessName,
                RecordingId = job.RecordingId
            });

            // Build pipeline options
            var pipelineContext = new PostProcessingContext
            {
                SourceLanguage = detectedLanguage ?? job.EffectiveLanguage,
                ActiveAppName = job.CapturedWindowTitle,
                ActiveAppProcessName = job.CapturedProcessName,
                ProfileName = job.ActiveWorkflow?.Name,
                AudioDurationSeconds = audioDuration
            };

            // Build LLM handler if the active workflow has prompt behavior.
            Func<string, CancellationToken, Task<string>>? llmHandler = null;
            if (job.ActiveWorkflow?.SystemPrompt(
                    fallbackTranslationTarget: job.ActiveWorkflow.Behavior.TranslationTarget,
                    detectedLanguage: detectedLanguage,
                    configuredLanguage: job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage) is { } systemPrompt)
            {
                if (_workflowTextProcessor.IsAnyProviderAvailable)
                {
                    var behavior = job.ActiveWorkflow.Behavior;
                    llmHandler = (text, token) => _workflowTextProcessor.ProcessAsync(
                        systemPrompt,
                        text,
                        behavior.ProviderOverride,
                        behavior.ModelOverride,
                        token);
                }
                else
                {
                    FeedbackText = Loc.Instance["Error.NoLlmProvider"];
                    FeedbackIsError = true;
                    ShowFeedback = true;
                }
            }

            // Build plugin post-processors (capture context in closure)
            var postProcessors = _modelManager.PluginManager.PostProcessors;
            var pluginProcessors = postProcessors.Select(p =>
                new PluginPostProcessor(p.Priority,
                    (text, token) => p.ProcessAsync(text, pipelineContext, token))).ToList();

            var translationTarget = job.ActiveWorkflow is null
                ? _settings.Current.TranslationTargetLanguage
                : null;

            var pipelineOptions = new PipelineOptions
            {
                AppFormatter = AppFormatterService.Format,
                TargetProcessName = job.CapturedProcessName,
                VocabularyBooster = GetVocabularyBooster(),
                DictionaryCorrector = _dictionary.ApplyCorrections,
                SnippetExpander = text => _snippets.ApplySnippets(text, () =>
                {
                    var t = "";
                    Application.Current.Dispatcher.Invoke(() =>
                        t = System.Windows.Clipboard.GetText());
                    return t;
                }),
                LlmHandler = llmHandler,
                TranslationHandler = !string.IsNullOrEmpty(translationTarget)
                    ? (text, src, tgt, token) => _translation.TranslateAsync(text, src, tgt, token)
                    : null,
                TranslationTarget = translationTarget,
                EffectiveSourceLanguage = job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage,
                DetectedLanguage = detectedLanguage,
                PluginPostProcessors = pluginProcessors,
                StatusCallback = async status =>
                {
                    var msg = status == "AI"
                        ? Loc.Instance["Status.AiPrompt"]
                        : Loc.Instance["Status.Translating"];
                    await Application.Current.Dispatcher.InvokeAsync(() => StatusText = msg);
                }
            };

            var pipelineResult = await _pipeline.ProcessAsync(rawText, pipelineOptions, ct);
            var finalText = pipelineResult.Text;

            var timestamp = DateTime.UtcNow;
            var engineUsed = ResolveEngineUsed(job.ActiveModelIdAtCapture);
            var wordsCount = CountWords(finalText);
            var recordId = job.ApiSessionId?.ToString() ?? Guid.NewGuid().ToString();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LastTranscribedText = finalText;
                LastTranscriptionLanguage = detectedLanguage;
            });
            _recentTranscriptions.RecordTranscription(
                recordId,
                finalText,
                timestamp,
                job.CapturedWindowTitle,
                job.CapturedProcessName);
            CompleteApiDictationSession(job.ApiSessionId, new ApiDictationTranscription(
                finalText,
                rawText,
                timestamp,
                job.CapturedWindowTitle,
                job.CapturedProcessName,
                job.CapturedUrl,
                audioDuration,
                detectedLanguage,
                engineUsed,
                job.ActiveModelIdAtCapture,
                wordsCount));

            // Save to history before output delivery so paste/action failures do not lose text.
            if (_settings.Current.SaveToHistoryEnabled)
            {
                string? audioFileName = null;
                try
                {
                    audioFileName = $"{Guid.NewGuid():N}.wav";
                    var audioPath = Path.Combine(TypeWhisperEnvironment.AudioPath, Path.GetFileName(audioFileName));
                    var wav = TypeWhisper.Core.Audio.WavEncoder.Encode(job.OriginalSamples);
                    await File.WriteAllBytesAsync(audioPath, wav, ct);
                }
                catch (IOException)
                {
                    audioFileName = null;
                }
                catch (UnauthorizedAccessException)
                {
                    audioFileName = null;
                }

                _history.AddRecord(new TranscriptionRecord
                {
                    Id = recordId,
                    Timestamp = timestamp,
                    RawText = rawText,
                    FinalText = finalText,
                    AppName = job.CapturedWindowTitle,
                    AppProcessName = job.CapturedProcessName,
                    AppUrl = job.CapturedUrl,
                    DurationSeconds = audioDuration,
                    Language = detectedLanguage,
                    ProfileName = job.ActiveWorkflow?.Name,
                    EngineUsed = engineUsed,
                    ModelUsed = job.ActiveModelIdAtCapture,
                    AudioFileName = audioFileName
                });
            }

            // Route to action plugin if configured
            var targetActionPluginId = job.ActiveWorkflow?.Output.TargetActionPluginId;

            InsertionResult insertResult;
            if (!string.IsNullOrEmpty(targetActionPluginId))
            {
                var actionPlugin = _modelManager.PluginManager.ActionPlugins
                    .FirstOrDefault(p => p.PluginId == targetActionPluginId || p.ActionId == targetActionPluginId);

                if (actionPlugin is not null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        TranscribedText = finalText;
                        State = DictationState.Processing;
                        StatusText = Loc.Instance.GetString("Status.ActionFormat", actionPlugin.ActionName);
                    });

                    var actionContext = new ActionContext(
                        job.CapturedWindowTitle,
                        job.CapturedProcessName,
                        null,
                        detectedLanguage,
                        rawText);
                    var actionResult = await actionPlugin.ExecuteAsync(finalText, actionContext, ct);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        FeedbackText = actionResult.Message ?? (actionResult.Success ? "Done" : "Failed");
                        FeedbackIsError = !actionResult.Success;
                        ShowFeedback = true;
                    });

                    insertResult = InsertionResult.ActionHandled;
                }
                else
                {
                    // Fallback to text insertion if action plugin not found
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        TranscribedText = finalText;
                        State = DictationState.Inserting;
                        StatusText = Loc.Instance["Status.Inserting"];
                    });
                    insertResult = await _textInsertion.InsertTextAsync(
                        finalText,
                        _settings.Current.AutoPaste,
                        job.ActiveWorkflow?.Output.AutoEnter == true,
                        job.CapturedWindowHandle);
                }
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    TranscribedText = finalText;
                    State = DictationState.Inserting;
                    StatusText = Loc.Instance["Status.Inserting"];
                });
                insertResult = await _textInsertion.InsertTextAsync(
                    finalText,
                    _settings.Current.AutoPaste,
                    job.ActiveWorkflow?.Output.AutoEnter == true,
                    job.CapturedWindowHandle);
            }

            _eventBus.Publish(new TextInsertedEvent
            {
                Text = finalText,
                TargetApp = job.CapturedProcessName,
                RecordingId = job.RecordingId
            });

            // Restore global model if workflow override was active
            if (job.ActiveModelIdAtCapture is not null
                && job.ActiveModelIdAtCapture != _settings.Current.SelectedModelId
                && _settings.Current.SelectedModelId is not null)
            {
                await _modelManager.LoadModelAsync(_settings.Current.SelectedModelId);
            }

            _sound.PlaySuccessSound();
            _speechFeedback.AnnounceTranscriptionComplete(finalText, detectedLanguage);
            _modelManager.ScheduleAutoUnload();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText = insertResult switch
                {
                    InsertionResult.Pasted => Loc.Instance["Status.Pasted"],
                    InsertionResult.CopiedToClipboard => Loc.Instance["Status.Clipboard"],
                    _ => Loc.Instance["Status.Done"]
                };
            });

            // Delay only for the last job when not recording
            if (_pendingJobCount <= 1 && !_isRecording)
            {
                var autoHideMilliseconds = AppSettings.NormalizePreviewBubbleAutoHideMilliseconds(
                    _settings.Current.PreviewBubbleAutoHideMilliseconds);
                if (autoHideMilliseconds > 0)
                    await Task.Delay(autoHideMilliseconds, ct);
            }
        }
        catch (OperationCanceledException)
        {
            FailApiDictationSession(job.ApiSessionId, Loc.Instance["Status.Cancelled"]);
            await Application.Current.Dispatcher.InvokeAsync(() =>
                ApplyTransientIdleFeedback(Loc.Instance["Status.Cancelled"]));
        }
        catch (Exception ex)
        {
            var apiSessionAlreadyCompleted = job.ApiSessionId is Guid completedApiSessionId
                && GetApiDictationSession(completedApiSessionId)?.Status == ApiDictationSessionStatus.Completed;
            if (!apiSessionAlreadyCompleted)
                FailApiDictationSession(job.ApiSessionId, ex.Message);

            _errorLog.AddEntry(ex.Message, ErrorCategory.Transcription);
            _eventBus.Publish(new TranscriptionFailedEvent
            {
                ErrorMessage = ex.Message,
                ModelId = job.ActiveModelIdAtCapture,
                AppName = job.CapturedWindowTitle,
                RecordingId = job.RecordingId
            });
            _sound.PlayErrorSound();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                State = DictationState.Error;
                StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
                FeedbackText = StatusText;
                FeedbackIsError = true;
                ShowFeedback = true;
            });
            try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { }
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateVisualState());
        }
    }

    private void UpdateVisualState()
    {
        if (_isRecording)
        {
            State = DictationState.Recording;
            IsOverlayVisible = true;
        }
        else if (_pendingJobCount > 0)
        {
            State = DictationState.Processing;
            IsOverlayVisible = true;
        }
        else
        {
            ResetSessionToIdle(clearFeedback: false, forceHotkeyStop: false);
            return;
        }

        _hotkey.IsCancelShortcutEnabled = ShouldEnableCancelShortcut(_isRecording, _pendingJobCount);
    }

    private async void OnSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        if (_vad is null || !_isRecording) return;

        if (!await _vadLock.WaitAsync(0)) return;
        try
        {
            _vad.AcceptWaveform(e.Samples);
            await ProcessVadSegments();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VAD error: {ex.Message}");
        }
        finally
        {
            _vadLock.Release();
        }
    }

    private async Task ProcessVadSegments()
    {
        if (_vad is null) return;

        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            _vad.Pop();
            _lastVadSegmentLength = segment.Samples.Length;

            if (segment.Samples.Length < 1600)
            {
                _lastVadDiscardedShortSegmentCount++;
                continue; // Skip very short segments
            }

            try
            {
                var result = await _modelManager.Engine.TranscribeAsync(segment.Samples);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    _partialSegments.Add(result.Text);
                    _lastVadFlushedSegmentCount++;
                    PartialText = string.Join(" ", _partialSegments);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Partial transcription error: {ex.Message}");
            }
        }
    }

    private static VoiceActivityDetector CreateVoiceActivityDetector()
    {
        var config = new VadModelConfig
        {
            SileroVad = new SileroVadModelConfig
            {
                Model = Path.Combine(AppContext.BaseDirectory, "Resources", "silero_vad.onnx"),
                Threshold = 0.5f,
                MinSilenceDuration = 0.5f,
                MinSpeechDuration = 0.25f,
            },
            SampleRate = 16000,
        };
        return new VoiceActivityDetector(config, 60);
    }

    [RelayCommand]
    private void CancelProcessing()
    {
        // Cancel current consumer, drain pending jobs
        _consumerCts.Cancel();

        while (_jobChannel.Reader.TryRead(out var pendingJob))
        {
            FailApiDictationSession(pendingJob.ApiSessionId, Loc.Instance["Status.Cancelled"]);
            DecrementPendingJobCount();
        }

        // Restart consumer with fresh CTS
        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ProcessJobsAsync(_consumerCts.Token));

        UpdateVisualState();
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        AudioLevel = e.RmsLevel;
    }

    private Func<string, string>? GetVocabularyBooster() =>
        _settings.Current.VocabularyBoostingEnabled ? _vocabularyBoosting.Apply : null;

    private string ResolveEngineUsed(string? activeModelId)
    {
        if (activeModelId is not null && ModelManagerService.IsPluginModel(activeModelId))
        {
            var (pluginId, _) = ModelManagerService.ParsePluginModelId(activeModelId);
            return _modelManager.PluginManager.TranscriptionEngines
                .FirstOrDefault(plugin => plugin.PluginId == pluginId)
                ?.ProviderId ?? activeModelId;
        }

        return "parakeet";
    }

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public void Dispose()
    {
        if (!_disposed)
        {
            _audioDucking.RestoreAudio();
            _mediaPause.ResumeMedia();
            _jobChannel.Writer.TryComplete();
            _consumerCts.Cancel();
            try { _consumerTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
            _consumerCts.Dispose();
            _durationTimer?.Dispose();
            _feedbackTimer?.Dispose();
            _streamingHandler.Dispose();
            _vad?.Dispose();
            _vadLock.Dispose();
            _modelManager.PluginManager.PluginStateChanged -= OnPluginStateChanged;
            _audio.AudioLevelChanged -= OnAudioLevelChanged;
            _audio.SamplesAvailable -= OnSamplesAvailable;
            _disposed = true;
        }
    }

    private void LogParakeetTailDiagnostics(RecordingTailDiagnosticSnapshot diagnostic)
    {
        if (!_settings.Current.InternalParakeetTailDiagnosticsEnabled ||
            !ParakeetTailHelper.IsParakeetModel(diagnostic.ModelId))
            return;

        var payload = new
        {
            model_id = diagnostic.ModelId,
            engine_type = diagnostic.EngineType,
            stop_requested_at_utc = diagnostic.StopRequestedAtUtc.ToString("o"),
            recording_duration_seconds = diagnostic.RecordingDurationSeconds,
            sample_count = diagnostic.SampleCount,
            last_samples_available_utc = diagnostic.LastSamplesAvailableUtc?.ToString("o"),
            silence_auto_stop_enabled = diagnostic.SilenceAutoStopEnabled,
            vad_flushed_segments = diagnostic.VadFlushedSegmentCount,
            vad_discarded_short_segments = diagnostic.VadDiscardedShortSegmentCount,
            last_vad_segment_length = diagnostic.LastVadSegmentLength,
            vad_contended_on_stop = diagnostic.VadWasContendedOnStop,
            tail_guard_applied = diagnostic.TailGuardApplied,
            tail_samples_added = diagnostic.TailSamplesAdded,
            final_text_source = diagnostic.FinalTextSource,
            full_decode_text_length = diagnostic.FullDecodeTextLength,
            partial_text_length = diagnostic.PartialTextLength,
            diverged_from_partials = diagnostic.DivergedFromPartials,
            full_decode_duration_ms = diagnostic.FullDecodeDurationMs,
            compare_decode_text_length = diagnostic.CompareDecodeTextLength,
            compare_decode_duration_ms = diagnostic.CompareDecodeDurationMs,
            recent_chunks = diagnostic.RecentChunks.Select(chunk => new
            {
                timestamp_utc = chunk.TimestampUtc.ToString("o"),
                peak = chunk.Peak,
                rms = chunk.Rms,
                pre_gain_rms = chunk.PreGainRms,
                sample_count = chunk.SampleCount
            })
        };

        _errorLog.AddEntry(JsonSerializer.Serialize(payload), "parakeet-tail");
    }

    private sealed record TranscriptionJob(
        float[] Samples,
        float[] OriginalSamples,
        List<string> PartialSegments,
        Workflow? ActiveWorkflow,
        IntPtr CapturedWindowHandle,
        string? CapturedProcessName,
        string? CapturedWindowTitle,
        string? CapturedUrl,
        string? EffectiveLanguage,
        TranscriptionTask EffectiveTask,
        string? ActiveModelIdAtCapture,
        Guid? ApiSessionId,
        bool TranscribeShortQuietClipsAggressively,
        RecordingTailDiagnosticSnapshot Diagnostic,
        Guid? RecordingId);
}

internal enum ShortSpeechDecision
{
    DiscardTooShort,
    DiscardNoSpeech,
    Transcribe
}

internal static class DictationFinalTextPolicy
{
    private const float NoSpeechProbabilityThreshold = 0.8f;
    private const int MinimumRepeatedPhraseWords = 3;
    private const int MinimumRepeatedPhraseCharacters = 8;
    private const int MaximumRepeatReductionPasses = 8;
    private static readonly Regex AutomaticEllipsisRegex = new(@"\s*(?:\.{3,}|\u2026)\s*", RegexOptions.CultureInvariant);

    public static string JoinPreviewSegments(IReadOnlyList<string> previewSegments) =>
        string.Join(" ", previewSegments.Where(segment => !string.IsNullOrWhiteSpace(segment))).Trim();

    public static bool ShouldRejectAsNoSpeech(
        string? finalText,
        float? noSpeechProbability,
        bool hasPreviewText,
        bool transcribeShortQuietClipsAggressively)
    {
        if (string.IsNullOrWhiteSpace(finalText))
            return !hasPreviewText;

        return noSpeechProbability is > NoSpeechProbabilityThreshold
            && !transcribeShortQuietClipsAggressively
            && !hasPreviewText;
    }

    public static string SelectRawText(string? finalText) =>
        NormalizeDictationArtifacts(finalText?.Trim() ?? "");

    public static string? SelectTrustedLiveText(string? liveText)
    {
        var text = liveText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static string SelectRawText(string? finalText, string? trustedLiveText)
    {
        var normalizedFinalText = SelectRawText(finalText);
        if (!string.IsNullOrWhiteSpace(normalizedFinalText))
            return normalizedFinalText;

        return SelectRawText(SelectTrustedLiveText(trustedLiveText));
    }

    private static string NormalizeDictationArtifacts(string text) =>
        RemoveAutomaticEllipses(ReduceAdjacentRepeatedPhrases(text));

    private static string RemoveAutomaticEllipses(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? ""
            : AutomaticEllipsisRegex.Replace(text, " ").Trim();

    private static string ReduceAdjacentRepeatedPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var reduced = text;
        for (var pass = 0; pass < MaximumRepeatReductionPasses; pass++)
        {
            var tokens = TokenizeWords(reduced);
            if (tokens.Count < MinimumRepeatedPhraseWords * 2)
                return reduced.Trim();

            if (!TryFindAdjacentRepeatedPhrase(reduced, tokens, out var removalStart, out var removalEnd))
                return reduced.Trim();

            reduced = string.Concat(reduced.AsSpan(0, removalStart), reduced.AsSpan(removalEnd)).Trim();
        }

        return reduced.Trim();
    }

    private static bool TryFindAdjacentRepeatedPhrase(
        string text,
        IReadOnlyList<WordToken> tokens,
        out int removalStart,
        out int removalEnd)
    {
        removalStart = 0;
        removalEnd = 0;

        for (var boundary = MinimumRepeatedPhraseWords; boundary <= tokens.Count - MinimumRepeatedPhraseWords; boundary++)
        {
            var maxLength = Math.Min(boundary, tokens.Count - boundary);
            for (var length = maxLength; length >= MinimumRepeatedPhraseWords; length--)
            {
                if (!HasMinimumRepeatedPhraseLength(tokens, boundary, length)
                    || !TokensMatch(tokens, boundary - length, boundary, length))
                {
                    continue;
                }

                if (RightMatchContinuesPhrase(text, tokens, boundary, length))
                {
                    removalStart = tokens[boundary - length].Start;
                    removalEnd = tokens[boundary].Start;
                }
                else
                {
                    removalStart = tokens[boundary].Start;
                    removalEnd = boundary + length < tokens.Count
                        ? tokens[boundary + length].Start
                        : text.Length;
                }

                return true;
            }
        }

        return false;
    }

    private static bool HasMinimumRepeatedPhraseLength(IReadOnlyList<WordToken> tokens, int boundary, int length)
    {
        var characterCount = 0;
        for (var i = boundary; i < boundary + length; i++)
            characterCount += tokens[i].Normalized.Length;

        return characterCount >= MinimumRepeatedPhraseCharacters;
    }

    private static bool TokensMatch(IReadOnlyList<WordToken> tokens, int leftStart, int rightStart, int length)
    {
        for (var offset = 0; offset < length; offset++)
        {
            if (!string.Equals(
                    tokens[leftStart + offset].Normalized,
                    tokens[rightStart + offset].Normalized,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RightMatchContinuesPhrase(string text, IReadOnlyList<WordToken> tokens, int boundary, int length)
    {
        var rightLastIndex = boundary + length - 1;
        if (rightLastIndex >= tokens.Count - 1)
            return false;

        var separator = text.AsSpan(tokens[rightLastIndex].End, tokens[rightLastIndex + 1].Start - tokens[rightLastIndex].End);
        foreach (var ch in separator)
        {
            if (ch is '.' or '!' or '?' or '\r' or '\n')
                return false;
        }

        return true;
    }

    private static List<WordToken> TokenizeWords(string text)
    {
        var tokens = new List<WordToken>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index]))
                index++;

            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index]))
                index++;

            if (start == index)
                continue;

            tokens.Add(new WordToken(start, index, NormalizeWord(text[start..index])));
        }

        return tokens;
    }

    private static string NormalizeWord(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var ch in word)
            builder.Append(char.ToLowerInvariant(ch));

        return builder.ToString();
    }

    private readonly record struct WordToken(int Start, int End, string Normalized);
}

internal static class DictationShortSpeechPolicy
{
    private const int SampleRate = 16000;
    private const double UltraShortTapSeconds = 0.04;
    private const double ShortClipSeconds = 1.0;
    private const double MinimumTranscriptionSeconds = 0.75;
    private const double TailPaddingSeconds = 0.3;
    private const float ShortClipQuietPeakThreshold = 0.003f;
    private const float LongClipQuietPeakThreshold = 0.006f;

    public static ShortSpeechDecision Classify(
        double rawDuration,
        float peakLevel,
        bool hasConfirmedText,
        bool transcribeShortQuietClipsAggressively = false)
    {
        if (rawDuration < UltraShortTapSeconds)
            return ShortSpeechDecision.DiscardTooShort;

        if (hasConfirmedText)
            return ShortSpeechDecision.Transcribe;

        if (rawDuration < ShortClipSeconds)
        {
            if (peakLevel < ShortClipQuietPeakThreshold)
            {
                return transcribeShortQuietClipsAggressively
                    ? ShortSpeechDecision.Transcribe
                    : ShortSpeechDecision.DiscardNoSpeech;
            }

            return ShortSpeechDecision.Transcribe;
        }

        if (peakLevel < LongClipQuietPeakThreshold)
        {
            return transcribeShortQuietClipsAggressively
                ? ShortSpeechDecision.Transcribe
                : ShortSpeechDecision.DiscardNoSpeech;
        }

        return ShortSpeechDecision.Transcribe;
    }

    public static float[] PadSamplesForFinalTranscription(float[] samples, double rawDuration)
    {
        if (rawDuration < MinimumTranscriptionSeconds)
        {
            var targetSampleCount = (int)(MinimumTranscriptionSeconds * SampleRate);
            return PadToSampleCount(samples, targetSampleCount);
        }

        var tailPadCount = (int)(TailPaddingSeconds * SampleRate);
        var paddedSamples = new float[samples.Length + tailPadCount];
        Array.Copy(samples, paddedSamples, samples.Length);
        return paddedSamples;
    }

    private static float[] PadToSampleCount(float[] samples, int targetSampleCount)
    {
        if (samples.Length >= targetSampleCount)
            return samples;

        var paddedSamples = new float[targetSampleCount];
        Array.Copy(samples, paddedSamples, samples.Length);
        return paddedSamples;
    }
}

internal sealed record RecordingTailDiagnosticSnapshot(
    string? ModelId,
    string EngineType,
    DateTime StopRequestedAtUtc,
    double RecordingDurationSeconds,
    int SampleCount,
    DateTime? LastSamplesAvailableUtc,
    IReadOnlyList<AudioChunkTelemetry> RecentChunks,
    bool SilenceAutoStopEnabled,
    int VadFlushedSegmentCount,
    int VadDiscardedShortSegmentCount,
    int LastVadSegmentLength,
    bool VadWasContendedOnStop,
    bool TailGuardApplied,
    int TailSamplesAdded,
    string? FinalTextSource,
    int? FullDecodeTextLength,
    int? PartialTextLength,
    bool? DivergedFromPartials,
    long? FullDecodeDurationMs,
    int? CompareDecodeTextLength,
    long? CompareDecodeDurationMs);

public enum DictationState
{
    Idle,
    Recording,
    Processing,
    Inserting,
    Error
}

