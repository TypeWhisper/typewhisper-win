using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

public sealed partial class FileTranscriptionQueueItemViewModel : ObservableObject
{
    public FileTranscriptionQueueItemViewModel(string filePath, FileTranscriptionQueueItemStatus status)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Status = status;
        StatusText = status == FileTranscriptionQueueItemStatus.Unsupported
            ? Loc.Instance["FileTranscription.UnsupportedFormat"]
            : Loc.Instance["FileTranscription.StatusQueued"];
        ErrorText = status == FileTranscriptionQueueItemStatus.Unsupported ? StatusText : "";
    }

    public string FilePath { get; }
    public string FileName { get; }
    public CancellationTokenSource? Cancellation { get; set; }
    public TranscriptionResult? RawResult { get; set; }

    [ObservableProperty] private FileTranscriptionQueueItemStatus _status;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;
    [ObservableProperty] private string _errorText = "";

    public bool IsProcessing => Status is FileTranscriptionQueueItemStatus.Loading or FileTranscriptionQueueItemStatus.Transcribing;
    public bool CanCancel => Status is FileTranscriptionQueueItemStatus.Queued or FileTranscriptionQueueItemStatus.Loading or FileTranscriptionQueueItemStatus.Transcribing;
    public bool HasResult => Status == FileTranscriptionQueueItemStatus.Completed && !string.IsNullOrWhiteSpace(ResultText);
    public bool CanExportSubtitles => HasResult && RawResult?.Segments is { Count: > 0 };
    public bool HasDetectedLanguage => !string.IsNullOrWhiteSpace(DetectedLanguage);
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    partial void OnStatusChanged(FileTranscriptionQueueItemStatus value)
    {
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(CanExportSubtitles));
    }

    partial void OnResultTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(CanExportSubtitles));
    }

    partial void OnDetectedLanguageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDetectedLanguage));
    }

    partial void OnErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public void RefreshExportState()
    {
        OnPropertyChanged(nameof(CanExportSubtitles));
    }
}
