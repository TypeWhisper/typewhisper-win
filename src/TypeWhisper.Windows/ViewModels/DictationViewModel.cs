using System.IO;
using System.Threading.Channels;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SherpaOnnx;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.Windows.ViewModels;

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
    private readonly ISnippetService _snippets;
    private readonly IProfileService _profiles;
    private readonly ITranslationService _translation;
    private readonly IAudioDuckingService _audioDucking;
    private readonly IMediaPauseService _mediaPause;
    private readonly PluginEventBus _eventBus;

    private CancellationTokenSource _consumerCts = new();
    private Task? _consumerTask;
    private System.Timers.Timer? _durationTimer;
    private bool _isRecording;
    private int _pendingJobCount;

    private readonly Channel<TranscriptionJob> _jobChannel =
        Channel.CreateBounded<TranscriptionJob>(new BoundedChannelOptions(5)
        { FullMode = BoundedChannelFullMode.Wait });

    // Captured at recording start for the current session
    private Profile? _activeProfile;
    private string? _capturedProcessName;
    private string? _capturedWindowTitle;

    // VAD for live transcription
    private VoiceActivityDetector? _vad;
    private readonly List<string> _partialSegments = [];
    private readonly SemaphoreSlim _vadLock = new(1, 1);
    private bool _disposed;

    [ObservableProperty] private DictationState _state = DictationState.Idle;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private string _statusText = "Bereit";
    [ObservableProperty] private string _transcribedText = "";
    [ObservableProperty] private HotkeyMode? _currentHotkeyMode;
    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private string? _activeProcessName;
    [ObservableProperty] private string? _activeProfileName;
    [ObservableProperty] private string _partialText = "";

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
        ISnippetService snippets,
        IProfileService profiles,
        ITranslationService translation,
        IAudioDuckingService audioDucking,
        IMediaPauseService mediaPause)
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
        _snippets = snippets;
        _profiles = profiles;
        _translation = translation;
        _audioDucking = audioDucking;
        _mediaPause = mediaPause;
        _eventBus = modelManager.PluginManager.EventBus;

        _consumerTask = Task.Run(() => ProcessJobsAsync(_consumerCts.Token));

        _audio.AudioLevelChanged += OnAudioLevelChanged;
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
                _audioDucking.RestoreAudio();
                _mediaPause.ResumeMedia();
                StatusText = $"Fehler: {ex.Message}";
                UpdateVisualState();
            }
        });
    }

    // Effective settings: profile override → global setting
    private string? EffectiveLanguage =>
        _activeProfile?.InputLanguage ?? _settings.Current.Language;

    private TranscriptionTask EffectiveTask =>
        (_activeProfile?.SelectedTask ?? _settings.Current.TranscriptionTask) == "translate"
            ? TranscriptionTask.Translate
            : TranscriptionTask.Transcribe;

    private bool EffectiveWhisperMode =>
        _activeProfile?.WhisperModeOverride ?? _settings.Current.WhisperModeEnabled;

    private string? EffectiveModelId =>
        _activeProfile?.TranscriptionModelOverride;

    [RelayCommand]
    private async Task StartRecording()
    {
        if (_isRecording) return;
        _isRecording = true;

        // Capture active window context at recording start
        _capturedProcessName = _activeWindow.GetActiveWindowProcessName();
        _capturedWindowTitle = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl();
        _activeProfile = _profiles.MatchProfile(_capturedProcessName, url);

        // Switch to profile model override if needed (cloud switch is instant)
        var profileModel = EffectiveModelId;
        if (profileModel is not null && profileModel != _modelManager.ActiveModelId)
        {
            try
            {
                await _modelManager.LoadModelAsync(profileModel);
            }
            catch (Exception ex)
            {
                StatusText = $"Modell-Fehler: {ex.Message}";
                _isRecording = false;
                return;
            }
        }

        if (!_modelManager.Engine.IsModelLoaded)
        {
            StatusText = "Kein Modell geladen";
            _isRecording = false;
            return;
        }

        ActiveProcessName = _capturedProcessName;
        ActiveProfileName = _activeProfile?.Name;

        _audio.WhisperModeEnabled = EffectiveWhisperMode;

        // Initialize VAD for live transcription (only for local models — cloud API
        // transcribes the full audio at once, VAD segments would be too slow/expensive)
        _partialSegments.Clear();
        PartialText = "";
        _vad?.Dispose();
        _vad = null;
        var useVad = _modelManager.ActiveModelId is null || !ModelManagerService.IsPluginModel(_modelManager.ActiveModelId);
        if (useVad)
        {
            _vad = CreateVoiceActivityDetector();
            _audio.SamplesAvailable += OnSamplesAvailable;
        }

        _sound.PlayStartSound();

        if (_settings.Current.AudioDuckingEnabled)
            _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
        if (_settings.Current.PauseMediaDuringRecording)
            _mediaPause.PauseMedia();

        _audio.StartRecording();
        _eventBus.Publish(new RecordingStartedEvent());

        State = DictationState.Recording;
        StatusText = "Aufnahme...";
        TranscribedText = "";
        CurrentHotkeyMode = _hotkey.CurrentMode;
        IsOverlayVisible = true;
        RecordingSeconds = 0;

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
        if (!_isRecording) return;
        _isRecording = false;

        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;

        _audio.SamplesAvailable -= OnSamplesAvailable;

        var samples = _audio.StopRecording();
        _eventBus.Publish(new RecordingStoppedEvent { DurationSeconds = _audio.RecordingDuration.TotalSeconds });
        _audioDucking.RestoreAudio();
        _mediaPause.ResumeMedia();
        RecordingSeconds = 0;

        // Flush remaining VAD segments
        List<string> partialSnapshot;
        if (_vad is not null)
        {
            _vad.Flush();
            await ProcessVadSegments();
            _vad.Dispose();
            _vad = null;
        }
        partialSnapshot = [.. _partialSegments];

        if (samples is null || samples.Length < 1600) // < 100ms
        {
            UpdateVisualState();
            StatusText = "Zu kurz";
            PartialText = "";
            _hotkey.ForceStop();
            return;
        }

        // Snapshot all context and enqueue — returns immediately
        var job = new TranscriptionJob(
            samples,
            partialSnapshot,
            _activeProfile,
            _capturedProcessName,
            _capturedWindowTitle,
            EffectiveLanguage,
            EffectiveTask,
            _modelManager.ActiveModelId);

        Interlocked.Increment(ref _pendingJobCount);
        await _jobChannel.Writer.WriteAsync(job);
        UpdateVisualState();
    }

    private async Task ProcessJobsAsync(CancellationToken ct)
    {
        await foreach (var job in _jobChannel.Reader.ReadAllAsync(ct))
        {
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateVisualState());
            await ProcessSingleJobAsync(job, ct);
            Interlocked.Decrement(ref _pendingJobCount);
            await Application.Current.Dispatcher.InvokeAsync(() => UpdateVisualState());
        }
    }

    private async Task ProcessSingleJobAsync(TranscriptionJob job, CancellationToken ct)
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                State = DictationState.Processing;
                StatusText = "Verarbeite...";
            });

            string rawText;
            string? detectedLanguage = null;
            double audioDuration = job.Samples.Length / 16000.0;

            if (job.PartialSegments.Count > 0)
            {
                rawText = string.Join(" ", job.PartialSegments);
            }
            else
            {
                var language = job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage;
                var result = await _modelManager.Engine.TranscribeAsync(
                    job.Samples, language, job.EffectiveTask, ct);

                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        StatusText = "Keine Sprache erkannt";
                        UpdateVisualState();
                    });
                    return;
                }

                rawText = result.Text;
                detectedLanguage = result.DetectedLanguage;
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusText = "Keine Sprache erkannt";
                    UpdateVisualState();
                });
                return;
            }

            _eventBus.Publish(new TranscriptionCompletedEvent
            {
                Text = rawText,
                DetectedLanguage = detectedLanguage,
                DurationSeconds = audioDuration,
                ModelId = job.ActiveModelIdAtCapture
            });

            // Apply corrections and snippets
            var finalText = _dictionary.ApplyCorrections(rawText);
            finalText = _snippets.ApplySnippets(finalText, () =>
            {
                var text = "";
                Application.Current.Dispatcher.Invoke(() =>
                    text = System.Windows.Clipboard.GetText());
                return text;
            });

            // Post-processor pipeline
            var postProcessors = _modelManager.PluginManager.PostProcessors;
            if (postProcessors.Count > 0)
            {
                var context = new PostProcessingContext
                {
                    SourceLanguage = detectedLanguage ?? job.EffectiveLanguage,
                    ActiveAppName = job.CapturedWindowTitle,
                    ActiveAppProcessName = job.CapturedProcessName,
                    ProfileName = job.ActiveProfile?.Name,
                    AudioDurationSeconds = audioDuration
                };
                foreach (var processor in postProcessors)
                {
                    finalText = await processor.ProcessAsync(finalText, context, ct);
                }
            }

            // Translate if configured
            var translationTarget = job.ActiveProfile?.TranslationTarget
                ?? _settings.Current.TranslationTargetLanguage;
            if (!string.IsNullOrEmpty(translationTarget))
            {
                var language = job.EffectiveLanguage == "auto" ? null : job.EffectiveLanguage;
                var sourceLang = detectedLanguage ?? language ?? "de";
                if (sourceLang != translationTarget)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() => StatusText = "Übersetze...");
                    finalText = await _translation.TranslateAsync(finalText, sourceLang, translationTarget, ct);
                }
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                TranscribedText = finalText;
                State = DictationState.Inserting;
                StatusText = "Einfügen...";
            });

            var insertResult = await _textInsertion.InsertTextAsync(
                finalText, _settings.Current.AutoPaste);

            _eventBus.Publish(new TextInsertedEvent
            {
                Text = finalText,
                TargetApp = job.CapturedProcessName
            });

            // Restore global model if profile override was active
            if (job.ActiveModelIdAtCapture is not null
                && job.ActiveModelIdAtCapture != _settings.Current.SelectedModelId
                && _settings.Current.SelectedModelId is not null)
            {
                await _modelManager.LoadModelAsync(_settings.Current.SelectedModelId);
            }

            // Save to history
            var engineUsed = job.ActiveModelIdAtCapture is not null && ModelManagerService.IsPluginModel(job.ActiveModelIdAtCapture)
                ? job.ActiveModelIdAtCapture
                : "parakeet";
            _history.AddRecord(new TranscriptionRecord
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                RawText = rawText,
                FinalText = finalText,
                AppName = job.CapturedWindowTitle,
                AppProcessName = job.CapturedProcessName,
                DurationSeconds = audioDuration,
                Language = detectedLanguage,
                ProfileName = job.ActiveProfile?.Name,
                EngineUsed = engineUsed
            });

            _sound.PlaySuccessSound();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText = insertResult switch
                {
                    InsertionResult.Pasted => "Eingefügt",
                    InsertionResult.CopiedToClipboard => "In Zwischenablage",
                    _ => "Fertig"
                };
            });

            // Delay only for the last job when not recording
            if (_pendingJobCount <= 1 && !_isRecording)
            {
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText = "Abgebrochen";
                UpdateVisualState();
            });
        }
        catch (Exception ex)
        {
            _eventBus.Publish(new TranscriptionFailedEvent
            {
                ErrorMessage = ex.Message,
                ModelId = job.ActiveModelIdAtCapture
            });
            _sound.PlayErrorSound();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                State = DictationState.Error;
                StatusText = $"Fehler: {ex.Message}";
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
            State = DictationState.Idle;
            StatusText = "Bereit";
            IsOverlayVisible = false;
            ActiveProcessName = null;
            ActiveProfileName = null;
            PartialText = "";
        }
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

            if (segment.Samples.Length < 1600) continue; // Skip very short segments

            try
            {
                var result = await _modelManager.Engine.TranscribeAsync(segment.Samples);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    _partialSegments.Add(result.Text);
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

        while (_jobChannel.Reader.TryRead(out _))
            Interlocked.Decrement(ref _pendingJobCount);

        // Restart consumer with fresh CTS
        _consumerCts = new CancellationTokenSource();
        _consumerTask = Task.Run(() => ProcessJobsAsync(_consumerCts.Token));

        UpdateVisualState();
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        AudioLevel = e.RmsLevel;
    }

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
            _vad?.Dispose();
            _vadLock.Dispose();
            _audio.AudioLevelChanged -= OnAudioLevelChanged;
            _audio.SamplesAvailable -= OnSamplesAvailable;
            _disposed = true;
        }
    }

    private sealed record TranscriptionJob(
        float[] Samples,
        List<string> PartialSegments,
        Profile? ActiveProfile,
        string? CapturedProcessName,
        string? CapturedWindowTitle,
        string? EffectiveLanguage,
        TranscriptionTask EffectiveTask,
        string? ActiveModelIdAtCapture);
}

public enum DictationState
{
    Idle,
    Recording,
    Processing,
    Inserting,
    Error
}

