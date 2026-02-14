using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.Windows.ViewModels;

public partial class FileTranscriptionViewModel : ObservableObject
{
    private readonly ModelManagerService _modelManager;
    private readonly ISettingsService _settings;
    private readonly AudioFileService _audioFile;

    private CancellationTokenSource? _cts;
    private TranscriptionResult? _lastResult;

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusText = "Datei hierher ziehen oder auswählen";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;

    public FileTranscriptionViewModel(
        ModelManagerService modelManager,
        ISettingsService settings,
        AudioFileService audioFile)
    {
        _modelManager = modelManager;
        _settings = settings;
        _audioFile = audioFile;
    }

    [RelayCommand]
    private async Task TranscribeFile(string? path)
    {
        var filePath = path ?? FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        if (!AudioFileService.IsSupported(filePath))
        {
            StatusText = "Nicht unterstütztes Format";
            return;
        }

        if (!_modelManager.Engine.IsModelLoaded)
        {
            StatusText = "Kein Modell geladen";
            return;
        }

        FilePath = filePath;
        IsProcessing = true;
        HasResult = false;
        ResultText = "";
        StatusText = "Lade Audio...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var samples = await _audioFile.LoadAudioAsync(filePath, _cts.Token);

            StatusText = "Transkribiere...";

            var s = _settings.Current;
            var language = s.Language == "auto" ? null : s.Language;
            var task = s.TranscriptionTask == "translate"
                ? TranscriptionTask.Translate
                : TranscriptionTask.Transcribe;

            var result = await _modelManager.Engine.TranscribeAsync(samples, language, task, _cts.Token);
            _lastResult = result;

            ResultText = result.Text;
            DetectedLanguage = result.DetectedLanguage;
            ProcessingTime = result.ProcessingTime;
            AudioDuration = result.Duration;
            HasResult = true;
            StatusText = $"Fertig in {result.ProcessingTime:F1}s ({result.Duration:F1}s Audio)";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Abgebrochen";
        }
        catch (Exception ex)
        {
            StatusText = $"Fehler: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(ResultText))
            System.Windows.Clipboard.SetText(ResultText);
    }

    [RelayCommand]
    private void ExportSrt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("srt", SubtitleExporter.ToSrt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportWebVtt()
    {
        if (_lastResult?.Segments is not { Count: > 0 }) return;
        ExportFile("vtt", SubtitleExporter.ToWebVtt(_lastResult.Segments));
    }

    [RelayCommand]
    private void ExportText()
    {
        if (string.IsNullOrEmpty(ResultText)) return;
        ExportFile("txt", ResultText);
    }

    private void ExportFile(string extension, string content)
    {
        var baseName = FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : "transcription";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{baseName}.{extension}",
            Filter = extension.ToUpperInvariant() + $" Files|*.{extension}|All Files|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            File.WriteAllText(dialog.FileName, content);
            StatusText = $"Exportiert: {Path.GetFileName(dialog.FileName)}";
        }
    }

    public void HandleFileDrop(string[] files)
    {
        if (files.Length > 0 && AudioFileService.IsSupported(files[0]))
        {
            TranscribeFileCommand.Execute(files[0]);
        }
    }
}
