namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Lists the supported file transcription queue item status values.
/// </summary>
public enum FileTranscriptionQueueItemStatus
{
    /// <summary>
    /// Represents the queued option.
    /// </summary>
    Queued,
    /// <summary>
    /// Represents the loading option.
    /// </summary>
    Loading,
    /// <summary>
    /// Represents the transcribing option.
    /// </summary>
    Transcribing,
    /// <summary>
    /// Represents the completed option.
    /// </summary>
    Completed,
    /// <summary>
    /// Represents the cancelled option.
    /// </summary>
    Cancelled,
    /// <summary>
    /// Represents the error option.
    /// </summary>
    Error,
    /// <summary>
    /// Represents the unsupported option.
    /// </summary>
    Unsupported
}
