using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SherpaOnnx;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

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

    private CancellationTokenSource? _processingCts;
    private System.Timers.Timer? _durationTimer;

    // Captured at recording start for the current session
    private Profile? _activeProfile;
    private string? _capturedProcessName;
    private string? _capturedWindowTitle;

    // VAD for live transcription
    private VoiceActivityDetector? _vad;
    private readonly List<string> _partialSegments = [];
    private readonly SemaphoreSlim _vadLock = new(1, 1);

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

        _audio.AudioLevelChanged += OnAudioLevelChanged;
        _hotkey.DictationStartRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(StartRecording);
        _hotkey.DictationStopRequested += (_, _) => Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await StopRecording();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StopRecording error: {ex}");
                _audioDucking.RestoreAudio();
                _mediaPause.ResumeMedia();
                State = DictationState.Idle;
                StatusText = $"Fehler: {ex.Message}";
                IsOverlayVisible = false;
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

    [RelayCommand]
    private void StartRecording()
    {
        if (State != DictationState.Idle) return;
        if (!_modelManager.Engine.IsModelLoaded)
        {
            StatusText = "Kein Modell geladen";
            return;
        }

        // Capture active window context at recording start
        _capturedProcessName = _activeWindow.GetActiveWindowProcessName();
        _capturedWindowTitle = _activeWindow.GetActiveWindowTitle();
        var url = _activeWindow.GetBrowserUrl();
        _activeProfile = _profiles.MatchProfile(_capturedProcessName, url);

        ActiveProcessName = _capturedProcessName;
        ActiveProfileName = _activeProfile?.Name;

        _audio.WhisperModeEnabled = EffectiveWhisperMode;

        // Initialize VAD for live transcription
        _partialSegments.Clear();
        PartialText = "";
        _vad?.Dispose();
        _vad = CreateVoiceActivityDetector();
        _audio.SamplesAvailable += OnSamplesAvailable;

        _sound.PlayStartSound();

        if (_settings.Current.AudioDuckingEnabled)
            _audioDucking.DuckAudio(_settings.Current.AudioDuckingLevel);
        if (_settings.Current.PauseMediaDuringRecording)
            _mediaPause.PauseMedia();

        _audio.StartRecording();

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
        if (State != DictationState.Recording) return;

        _durationTimer?.Stop();
        _durationTimer?.Dispose();
        _durationTimer = null;

        _audio.SamplesAvailable -= OnSamplesAvailable;

        var samples = _audio.StopRecording();
        _audioDucking.RestoreAudio();
        _mediaPause.ResumeMedia();
        RecordingSeconds = 0;

        // Flush remaining VAD segments
        if (_vad is not null)
        {
            _vad.Flush();
            await ProcessVadSegments();
            _vad.Dispose();
            _vad = null;
        }

        if (samples is null || samples.Length < 1600) // < 100ms
        {
            State = DictationState.Idle;
            StatusText = "Zu kurz";
            IsOverlayVisible = false;
            ActiveProcessName = null;
            ActiveProfileName = null;
            PartialText = "";
            _hotkey.ForceStop();
            return;
        }

        await ProcessAndInsertAsync(samples);
    }

    private async Task ProcessAndInsertAsync(float[] samples)
    {
        State = DictationState.Processing;
        StatusText = "Verarbeite...";

        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();

        try
        {
            // Use partial segments if available, otherwise transcribe full audio
            string rawText;
            string? detectedLanguage = null;
            double audioDuration = samples.Length / 16000.0;

            if (_partialSegments.Count > 0)
            {
                rawText = string.Join(" ", _partialSegments);
            }
            else
            {
                var language = EffectiveLanguage == "auto" ? null : EffectiveLanguage;
                var task = EffectiveTask;
                var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, _processingCts.Token);

                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    State = DictationState.Idle;
                    StatusText = "Keine Sprache erkannt";
                    IsOverlayVisible = false;
                    ActiveProcessName = null;
                    ActiveProfileName = null;
                    PartialText = "";
                    return;
                }

                rawText = result.Text;
                detectedLanguage = result.DetectedLanguage;
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                State = DictationState.Idle;
                StatusText = "Keine Sprache erkannt";
                IsOverlayVisible = false;
                ActiveProcessName = null;
                ActiveProfileName = null;
                PartialText = "";
                return;
            }

            // Apply corrections and snippets
            var finalText = _dictionary.ApplyCorrections(rawText);
            finalText = _snippets.ApplySnippets(finalText);

            // Translate if configured
            var translationTarget = _activeProfile?.TranslationTarget
                ?? _settings.Current.TranslationTargetLanguage;
            if (!string.IsNullOrEmpty(translationTarget))
            {
                var language = EffectiveLanguage == "auto" ? null : EffectiveLanguage;
                var sourceLang = detectedLanguage ?? language ?? "de";
                if (sourceLang != translationTarget)
                {
                    StatusText = "Übersetze...";
                    finalText = await _translation.TranslateAsync(finalText, sourceLang, translationTarget, _processingCts.Token);
                }
            }

            TranscribedText = finalText;

            // Insert text
            State = DictationState.Inserting;
            StatusText = "Einfügen...";

            var insertResult = await _textInsertion.InsertTextAsync(
                finalText, _settings.Current.AutoPaste);

            // Save to history
            _history.AddRecord(new TranscriptionRecord
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                RawText = rawText,
                FinalText = finalText,
                AppName = _capturedWindowTitle,
                AppProcessName = _capturedProcessName,
                DurationSeconds = audioDuration,
                Language = detectedLanguage,
                ProfileName = _activeProfile?.Name,
                EngineUsed = "parakeet"
            });

            _sound.PlaySuccessSound();
            StatusText = insertResult switch
            {
                InsertionResult.Pasted => "Eingefügt",
                InsertionResult.CopiedToClipboard => "In Zwischenablage",
                _ => "Fertig"
            };

            // Brief display then hide
            await Task.Delay(1500);
            State = DictationState.Idle;
            IsOverlayVisible = false;
            StatusText = "Bereit";
            ActiveProcessName = null;
            ActiveProfileName = null;
            PartialText = "";
        }
        catch (OperationCanceledException)
        {
            State = DictationState.Idle;
            StatusText = "Abgebrochen";
            IsOverlayVisible = false;
            ActiveProcessName = null;
            ActiveProfileName = null;
            PartialText = "";
        }
        catch (Exception ex)
        {
            _sound.PlayErrorSound();
            State = DictationState.Error;
            StatusText = $"Fehler: {ex.Message}";
            await Task.Delay(3000);
            State = DictationState.Idle;
            IsOverlayVisible = false;
            StatusText = "Bereit";
            ActiveProcessName = null;
            ActiveProfileName = null;
            PartialText = "";
        }
    }

    private async void OnSamplesAvailable(object? sender, SamplesAvailableEventArgs e)
    {
        if (_vad is null || State != DictationState.Recording) return;

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
        _processingCts?.Cancel();
    }

    private void OnAudioLevelChanged(object? sender, AudioLevelEventArgs e)
    {
        AudioLevel = e.RmsLevel;
    }

    public void Dispose()
    {
        _audioDucking.RestoreAudio();
        _mediaPause.ResumeMedia();
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _durationTimer?.Dispose();
        _vad?.Dispose();
        _vadLock.Dispose();
        _audio.AudioLevelChanged -= OnAudioLevelChanged;
        _audio.SamplesAvailable -= OnSamplesAvailable;
    }
}

public enum DictationState
{
    Idle,
    Recording,
    Processing,
    Inserting,
    Error
}
