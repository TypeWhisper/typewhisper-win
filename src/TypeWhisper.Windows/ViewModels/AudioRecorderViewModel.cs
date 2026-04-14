using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core;
using TypeWhisper.Core.Audio;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public sealed record RecordingItem(string FileName, string FilePath, DateTime CreatedAt, TimeSpan Duration, string? Transcript);

public partial class AudioRecorderViewModel : ObservableObject, IDisposable
{
    private readonly AudioRecordingService _audio;
    private readonly ModelManagerService _modelManager;
    private System.Timers.Timer? _timer;
    private DateTime _recordingStart;
    private string? _statusKey;

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private float _audioLevel;
    [ObservableProperty] private string _statusText = Loc.Instance["Status.Ready"];
    [ObservableProperty] private bool _isTranscribing;

    public ObservableCollection<RecordingItem> Recordings { get; } = [];
    public bool HasRecordings => Recordings.Count > 0;

    public AudioRecorderViewModel(AudioRecordingService audio, ModelManagerService modelManager)
    {
        _audio = audio;
        _modelManager = modelManager;
        _audio.AudioLevelChanged += (_, e) =>
            Application.Current?.Dispatcher.InvokeAsync(() => AudioLevel = e.PeakLevel);
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
            Application.Current?.Dispatcher.InvokeAsync(() =>
                DurationText = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}");
        };
        _timer.Start();
    }

    private async void StopRecording()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;

        var samples = _audio.StopRecording();
        IsRecording = false;
        var duration = DateTime.UtcNow - _recordingStart;

        if (samples.Length == 0)
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
        try
        {
            var engine = _modelManager.ActiveTranscriptionPlugin;
            if (engine is not null)
            {
                var result = await engine.TranscribeAsync(wav, null, false, null, CancellationToken.None);
                transcript = result.Text;
                if (!string.IsNullOrEmpty(transcript))
                {
                    var txtPath = Path.ChangeExtension(filePath, ".txt");
                    await File.WriteAllTextAsync(txtPath, transcript);
                }
            }
        }
        catch { }

        IsTranscribing = false;

        var item = new RecordingItem(fileName, filePath, DateTime.Now, duration, transcript);
        Application.Current?.Dispatcher.Invoke(() => Recordings.Insert(0, item));
        SetLocalizedStatus(transcript is not null ? "Status.Done" : "Recorder.SavedNoModel");
        DurationText = "0:00";
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

        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = Loc.Instance[_statusKey];
        });
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _timer?.Dispose();
    }
}
