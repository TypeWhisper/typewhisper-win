using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Provides file transcription queue item view model behavior.
/// </summary>
public sealed partial class FileTranscriptionQueueItemViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the FileTranscriptionQueueItemViewModel class.
    /// </summary>
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

    /// <summary>
    /// Gets the file path.
    /// </summary>
    public string FilePath { get; }
    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName { get; }
    /// <summary>
    /// Gets or sets the cancellation value.
    /// </summary>
    public CancellationTokenSource? Cancellation { get; set; }
    /// <summary>
    /// Gets or sets the raw result value.
    /// </summary>
    public TranscriptionResult? RawResult { get; set; }

    [ObservableProperty] private FileTranscriptionQueueItemStatus _status;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string? _detectedLanguage;
    [ObservableProperty] private double _processingTime;
    [ObservableProperty] private double _audioDuration;
    [ObservableProperty] private string _errorText = "";

    /// <summary>
    /// Gets whether is processing.
    /// </summary>
    public bool IsProcessing => Status is FileTranscriptionQueueItemStatus.Loading or FileTranscriptionQueueItemStatus.Transcribing;
    /// <summary>
    /// Gets whether can cancel.
    /// </summary>
    public bool CanCancel => Status is FileTranscriptionQueueItemStatus.Queued or FileTranscriptionQueueItemStatus.Loading or FileTranscriptionQueueItemStatus.Transcribing;
    /// <summary>
    /// Returns whether result.
    /// </summary>
    public bool HasResult => Status == FileTranscriptionQueueItemStatus.Completed && !string.IsNullOrWhiteSpace(ResultText);
    /// <summary>
    /// Gets whether can export subtitles.
    /// </summary>
    public bool CanExportSubtitles => HasResult && RawResult?.Segments is { Count: > 0 };
    /// <summary>
    /// Returns whether detected language.
    /// </summary>
    public bool HasDetectedLanguage => !string.IsNullOrWhiteSpace(DetectedLanguage);
    /// <summary>
    /// Returns whether error.
    /// </summary>
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

    /// <summary>
    /// Refreshes export state.
    /// </summary>
    public void RefreshExportState()
    {
        OnPropertyChanged(nameof(CanExportSubtitles));
    }
}
