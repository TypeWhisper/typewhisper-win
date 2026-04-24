using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

public enum WatchFolderOutputFormat
{
    Markdown,
    PlainText,
    Srt,
    Vtt
}

public sealed record WatchFolderOptions(
    string WatchPath,
    string? OutputPath,
    WatchFolderOutputFormat OutputFormat,
    bool DeleteSource);

public sealed record WatchFolderTranscriptionRequest(string FilePath);

public sealed record WatchFolderTranscriptionResult(
    string Text,
    string? DetectedLanguage,
    double Duration,
    double ProcessingTime,
    IReadOnlyList<TranscriptionSegment> Segments,
    string? EngineId,
    string? ModelId);

public sealed record WatchFolderExportArtifact(string FileExtension, string Content);

public sealed record WatchFolderHistoryItem
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required DateTime ProcessedAtUtc { get; init; }
    public required string OutputPath { get; init; }
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public static class WatchFolderOutputFormats
{
    public static WatchFolderOutputFormat Parse(string? storedValue) =>
        string.Equals(storedValue, "txt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.PlainText :
        string.Equals(storedValue, "srt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.Srt :
        string.Equals(storedValue, "vtt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.Vtt :
        WatchFolderOutputFormat.Markdown;

    public static string ToStoredValue(WatchFolderOutputFormat format) => format switch
    {
        WatchFolderOutputFormat.PlainText => "txt",
        WatchFolderOutputFormat.Srt => "srt",
        WatchFolderOutputFormat.Vtt => "vtt",
        _ => "md"
    };

    public static string ToFileExtension(WatchFolderOutputFormat format) => ToStoredValue(format);
}
