using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    private CancellationTokenSource? _processingCts;
    private System.Timers.Timer? _durationTimer;

    // Captured at recording start for the current session
    private Profile? _activeProfile;
    private string? _capturedProcessName;
    private string? _capturedWindowTitle;

    [ObservableProperty] private DictationState _state = DictationState.Idle;
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private double _recordingSeconds;
    [ObservableProperty] private string _statusText = "Bereit";
    [ObservableProperty] private string _transcribedText = "";
    [ObservableProperty] private HotkeyMode? _currentHotkeyMode;
    [ObservableProperty] private bool _isOverlayVisible;

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
        ITranslationService translation)
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

        _audio.WhisperModeEnabled = EffectiveWhisperMode;
        _audio.StartRecording();
        _sound.PlayStartSound();

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

        var samples = _audio.StopRecording();
        _sound.PlayStopSound();
        RecordingSeconds = 0;

        if (samples is null || samples.Length < 1600) // < 100ms
        {
            State = DictationState.Idle;
            StatusText = "Zu kurz";
            IsOverlayVisible = false;
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
            var language = EffectiveLanguage == "auto" ? null : EffectiveLanguage;
            var task = EffectiveTask;

            var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, _processingCts.Token);

            if (string.IsNullOrWhiteSpace(result.Text))
            {
                State = DictationState.Idle;
                StatusText = "Keine Sprache erkannt";
                IsOverlayVisible = false;
                return;
            }

            var rawText = result.Text;

            // Apply corrections and snippets
            var finalText = _dictionary.ApplyCorrections(rawText);
            finalText = _snippets.ApplySnippets(finalText);

            // Translate if configured
            var translationTarget = _activeProfile?.TranslationTarget
                ?? _settings.Current.TranslationTargetLanguage;
            if (!string.IsNullOrEmpty(translationTarget))
            {
                var sourceLang = result.DetectedLanguage ?? language ?? "de";
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
                DurationSeconds = result.Duration,
                Language = result.DetectedLanguage,
                EngineUsed = _modelManager.ActiveEngineType == EngineType.Parakeet ? "parakeet" : "whisper"
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
        }
        catch (OperationCanceledException)
        {
            State = DictationState.Idle;
            StatusText = "Abgebrochen";
            IsOverlayVisible = false;
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
        }
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
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _durationTimer?.Dispose();
        _audio.AudioLevelChanged -= OnAudioLevelChanged;
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
