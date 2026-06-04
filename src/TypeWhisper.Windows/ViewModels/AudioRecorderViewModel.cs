using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Audio;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK.Models;
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
public sealed record RecordingItem(string FileName, string FilePath, DateTime CreatedAt, TimeSpan Duration, string? Transcript);

/// <summary>
/// Provides audio recorder view model behavior.
/// </summary>
public partial class AudioRecorderViewModel : ObservableObject, IDisposable
{
    private readonly AudioRecordingService _audio;
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
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
        IPostProcessingPipeline pipeline,
        ITranslationService translation)
    {
        _audio = audio;
        _modelManager = modelManager;
        _settings = settings;
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

        SetLocalizedStatus("Recorder.SavedTranscribing");
        IsTranscribing = true;

        // Auto-transcribe
        string? transcript = null;
        var processingFailed = false;
        try
        {
            var engine = _modelManager.ActiveTranscriptionPlugin;
            if (engine is not null)
            {
                var result = await engine.TranscribeAsync(wav, null, false, null, CancellationToken.None);
                transcript = await PostProcessTranscriptAsync(result);
                if (!string.IsNullOrEmpty(transcript))
                {
                    var txtPath = Path.ChangeExtension(filePath, ".txt");
                    await File.WriteAllTextAsync(txtPath, transcript);
                }
            }
        }
        catch (Exception ex)
        {
            processingFailed = true;
            StatusText = Loc.Instance.GetString("Status.ErrorFormat", ex.Message);
        }

        IsTranscribing = false;

        var item = new RecordingItem(fileName, filePath, DateTime.Now, duration, transcript);
        DispatchToUi(() => Recordings.Insert(0, item));
        if (!processingFailed)
            SetLocalizedStatus(transcript is not null ? "Status.Done" : "Recorder.SavedNoModel");
        DurationText = "0:00";
    }

    private async Task<string> PostProcessTranscriptAsync(PluginTranscriptionResult result)
    {
        var translationTarget = _settings.Current.TranslationTargetLanguage;
        var pipelineResult = await _pipeline.ProcessAsync(result.Text, new PipelineOptions
        {
            TranslationHandler = !string.IsNullOrEmpty(translationTarget)
                ? (text, src, tgt, token) => _translation.TranslateAsync(text, src, tgt, token)
                : null,
            TranslationTarget = translationTarget,
            RequireTranslationSuccess = !string.IsNullOrEmpty(translationTarget),
            DetectedLanguage = result.DetectedLanguage
        }, CancellationToken.None);

        return pipelineResult.Text;
    }

    [RelayCommand]
    private void DeleteRecording(RecordingItem item)
    {
        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            var txt = Path.ChangeExtension(item.FilePath, ".txt");
            if (File.Exists(txt)) File.Delete(txt);
        }
        catch { }
        Recordings.Remove(item);
    }

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

    private void SetLocalizedStatus(string key)
    {
        _statusKey = key;
        StatusText = Loc.Instance[key];
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

        dispatcher.Invoke(action);
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
