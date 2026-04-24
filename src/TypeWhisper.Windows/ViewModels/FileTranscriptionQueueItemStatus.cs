namespace TypeWhisper.Windows.ViewModels;

public enum FileTranscriptionQueueItemStatus
{
    Queued,
    Loading,
    Transcribing,
    Completed,
    Cancelled,
    Error,
    Unsupported
}
