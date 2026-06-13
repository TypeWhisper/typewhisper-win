using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Represents recording item data.
/// </summary>
/// <param name="FileName">File name supplied to the member.</param>
/// <param name="FilePath">File path supplied to the member.</param>
/// <param name="CreatedAt">Created at supplied to the member.</param>
/// <param name="Duration">Duration supplied to the member.</param>
/// <param name="Transcript">Transcript supplied to the member.</param>
/// <param name="IsTranscribing">Transcription progress flag supplied to the member.</param>
/// <param name="ErrorMessage">Failure message supplied to the member.</param>
public sealed record RecordingItem(
    string FileName,
    string FilePath,
    DateTime CreatedAt,
    TimeSpan Duration,
    string? Transcript,
    bool IsTranscribing = false,
    string? ErrorMessage = null)
{
    /// <summary>
    /// Gets whether a per-recording status line should be shown.
    /// </summary>
    public bool HasStatus => IsTranscribing || HasError;
    /// <summary>
    /// Gets whether this recording has a transcription failure.
    /// </summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    /// <summary>
    /// Gets the per-recording status text.
    /// </summary>
    public string ItemStatusText => IsTranscribing
        ? Loc.Instance["Recorder.TranscribingItem"]
        : ErrorMessage ?? "";
    /// <summary>
    /// Gets whether this recording can be transcribed.
    /// </summary>
    public bool CanTranscribe => !IsTranscribing && File.Exists(FilePath);
    /// <summary>
    /// Gets the tooltip for the transcription command.
    /// </summary>
    public string TranscribeToolTip => HasError
        ? Loc.Instance["Recorder.Retry"]
        : Loc.Instance["Recorder.Transcribe"];
}

/// <summary>
/// Provides audio recorder view model behavior.
/// </summary>
public partial class AudioRecorderViewModel : ObservableObject, IDisposable
{
    private readonly AudioRecordingService _audio;
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;
    private readonly IErrorLogService _errorLog;
    private readonly IPostProcessingPipeline _pipeline;
    private readonly ITranslationService _translation;
    private System.Timers.Timer? _timer;
    private DateTime _recordingStart;
    private string? _statusKey;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private string _statusText = Loc.Instance["Status.Ready"];
    [ObservableProperty] private bool _isTranscribing;

    /// <summary>
    /// Gets the recordings.
    /// </summary>
    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    /// <summary>
    /// Gets whether has recordings.
    /// </summary>
    public bool HasRecordings => Recordings.Count > 0;

    /// <summary>
    /// Initializes a new instance of the AudioRecorderViewModel class.
    /// </summary>
    public AudioRecorderViewModel(
        AudioRecordingService audio,
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile,
        IErrorLogService errorLog,
        IPostProcessingPipeline pipeline,
        ITranslationService translation)
    {
        _audio = audio;
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
        _errorLog = errorLog;
        _pipeline = pipeline;
        _translation = translation;
        _audio.AudioLevelChanged += (_, e) =>
            DispatchToUi(() => AudioLevel = e.PeakLevel);
        _statusKey = "Status.Ready";
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        LoadExistingRecordings();
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (!_audio.WarmUp())
        {
            SetLocalizedStatus("Status.NoMicrophone");
            return;
        }

        _audio.StartRecording();
        IsRecording = true;
        _recordingStart = DateTime.UtcNow;
        SetLocalizedStatus("Status.Recording");

        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            var elapsed = DateTime.UtcNow - _recordingStart;
            DispatchToUi(() =>
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}");
        };
        _timer.Start();
    }

    private async void StopRecording()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;

        var samples = await _audio.StopRecordingAsync();
        IsRecording = false;
        var duration = DateTime.UtcNow - _recordingStart;

        if (samples is null || samples.Length == 0)
        {
            SetLocalizedStatus("Recorder.NoAudioCaptured");
            return;
        }

        // Save WAV
        var fileName = $"recording-{DateTime.Now:yyyy-MM-dd-HHmmss}.wav";
        var filePath = Path.Combine(TypeWhisperEnvironment.AudioPath, fileName);
        var wav = WavEncoder.Encode(samples);
        await File.WriteAllBytesAsync(filePath, wav);

        var item = new RecordingItem(fileName, filePath, DateTime.Now, duration, null, IsTranscribing: true);
        DispatchToUi(() =>
        {
            Recordings.Insert(0, item);
            OnPropertyChanged(nameof(HasRecordings));
            RefreshRecordingCommandState();
        });

        await TranscribeRecordingAsync(
            item,
            _ => Task.FromResult(samples),
            CancellationToken.None);
        DurationText = "0:00";
    }

    private async Task<string> PostProcessTranscriptAsync(
        TranscriptionResult result,
        TranscriptionTask task,
        string? configuredLanguage,
        CancellationToken ct)
    {
        var currentSettings = _settings.Current;
        var translationTarget = currentSettings.TranslationTargetLanguage;
        var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            TranscriptionNumberNormalizationEnabled = currentSettings.TranscriptionNumberNormalizationEnabled,
            TranscriptionTask = task,
            ConfiguredLanguage = configuredLanguage,
            TranslationHandler = !string.IsNullOrEmpty(translationTarget)
                ? (text, src, tgt, token) => _translation.TranslateAsync(text, src, tgt, token)
                : null,
            TranslationTarget = translationTarget,
            RequireTranslationSuccess = !string.IsNullOrEmpty(translationTarget),
            DetectedLanguage = result.DetectedLanguage
        }, ct);

        return pipelineResult.Text;
    }

    [RelayCommand(CanExecute = nameof(CanTranscribeRecording))]
    private async Task TranscribeRecordingAsync(RecordingItem? item)
    {
        if (item is null)
            return;

        await TranscribeRecordingAsync(
            item,
            token => _audioFile.LoadAudioAsync(item.FilePath, token),
            CancellationToken.None);
    }

    private static bool CanTranscribeRecording(RecordingItem? item) =>
        item?.CanTranscribe == true;

    private async Task TranscribeRecordingAsync(
        RecordingItem item,
        Func<CancellationToken, Task<float[]>> loadSamples,
        CancellationToken ct)
    {
        var current = ReplaceRecording(item, item with { IsTranscribing = true, ErrorMessage = null });
        SetLocalizedStatus("Recorder.SavedTranscribing");

        try
        {
            var settings = _settings.Current;
            if (string.IsNullOrWhiteSpace(settings.SelectedModelId))
            {
                ReplaceRecording(current, current with { IsTranscribing = false });
                SetLocalizedStatus("Recorder.SavedNoModel");
                return;
            }

            var samples = await loadSamples(ct);
            if (samples.Length == 0)
                throw new InvalidOperationException(Loc.Instance["Recorder.NoAudioCaptured"]);

            await using var modelScope = await _modelManager.BeginTranscriptionRequestAsync(
                null,
                null,
                false,
                ct);

            var configuredLanguage = settings.Language == "auto" ? null : settings.Language;
            var task = settings.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;
            var activeResult = await _modelManager.TranscribeActiveAsync(
                samples,
                configuredLanguage,
                task,
                prompt: null,
                ct);
            var transcript = await PostProcessTranscriptAsync(activeResult.Result, task, configuredLanguage, ct);

            if (!string.IsNullOrEmpty(transcript))
            {
                var txtPath = Path.ChangeExtension(current.FilePath, ".txt");
                await File.WriteAllTextAsync(txtPath, transcript, ct);
            }

            ReplaceRecording(current, current with
            {
                Transcript = transcript,
                IsTranscribing = false,
                ErrorMessage = null
            });
            SetLocalizedStatus("Status.Done");
        }
        catch (OperationCanceledException)
        {
            ReplaceRecording(current, current with
            {
                IsTranscribing = false,
                ErrorMessage = Loc.Instance["Status.Cancelled"]
            });
            throw;
        }
        catch (IOException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (InvalidOperationException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (ModelManagerRequestException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (NotSupportedException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            HandleTranscriptionFailure(current, ex);
        }
        finally
        {
            _modelManager.ScheduleAutoUnload();
            DispatchToUi(RefreshRecordingCommandState);
        }
    }

    private void HandleTranscriptionFailure(RecordingItem current, Exception ex)
    {
        var message = ex.Message;
        _errorLog.AddEntry(message, ErrorCategory.Transcription);
        ReplaceRecording(current, current with
        {
            IsTranscribing = false,
            ErrorMessage = Loc.Instance.GetString("Recorder.FailedFormat", message)
        });
        SetErrorStatus(message);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteRecording))]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null || item.IsTranscribing)
            return;

        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            var txt = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txt)) File.Delete(txt);
        }
        catch { }
        Recordings.Remove(item);
        OnPropertyChanged(nameof(HasRecordings));
        RefreshRecordingCommandState();
    }

    private static bool CanDeleteRecording(RecordingItem? item) =>
        item is { IsTranscribing: false };

    private void LoadExistingRecordings()
    {
        try
        {
            var dir = TypeWhisperEnvironment.AudioPath;
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, "recording-*.wav").OrderByDescending(f => f))
            {
                var fi = new FileInfo(file);
                var txtFile = Path.ChangeExtension(file, ".txt");
                var transcript = File.Exists(txtFile) ? File.ReadAllText(txtFile) : null;
                Recordings.Add(new RecordingItem(fi.Name, file, fi.CreationTime, TimeSpan.Zero, transcript));
            }
        }
        catch { }
    }

    private RecordingItem ReplaceRecording(RecordingItem item, RecordingItem replacement)
    {
        DispatchToUi(() =>
        {
            var index = Recordings.IndexOf(item);
            if (index < 0)
            {
                for (var i = 0; i < Recordings.Count; i++)
                {
                    if (string.Equals(Recordings[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }
            }

            if (index >= 0)
                Recordings[index] = replacement;

            RefreshRecordingCommandState();
        });

        return replacement;
    }

    private void RefreshRecordingCommandState()
    {
        IsTranscribing = Recordings.Any(item => item.IsTranscribing);
        TranscribeRecordingCommand.NotifyCanExecuteChanged();
        DeleteRecordingCommand.NotifyCanExecuteChanged();
    }

    private void SetLocalizedStatus(string key)
    {
        _statusKey = key;
        StatusText = Loc.Instance[key];
    }

    private void SetErrorStatus(string message)
    {
        _statusKey = null;
        StatusText = Loc.Instance.GetString("Status.ErrorFormat", message);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_statusKey))
            return;

        DispatchToUi(() =>
        {
            StatusText = Loc.Instance[_statusKey];
        });
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.InvokeAsync(action);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _timer?.Dispose();
    }
}
