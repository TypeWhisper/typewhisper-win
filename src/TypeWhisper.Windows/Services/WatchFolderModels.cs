using TypeWhisper.Core.Models;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Lists the supported watch folder output format values.
/// </summary>
public enum WatchFolderOutputFormat
{
    /// <summary>
    /// Represents the markdown option.
    /// </summary>
    Markdown,
    /// <summary>
    /// Represents the plain text option.
    /// </summary>
    PlainText,
    /// <summary>
    /// Represents the SRT option.
    /// </summary>
    Srt,
    /// <summary>
    /// Represents the VTT option.
    /// </summary>
    Vtt
}

/// <summary>
/// Represents watch folder options data.
/// </summary>
/// <param name="WatchPath">Watch path supplied to the member.</param>
/// <param name="OutputPath">Output path supplied to the member.</param>
/// <param name="OutputFormat">Output format supplied to the member.</param>
/// <param name="DeleteSource">Delete source supplied to the member.</param>
public sealed record WatchFolderOptions(
    string WatchPath,
    string? OutputPath,
    WatchFolderOutputFormat OutputFormat,
    bool DeleteSource);

/// <summary>
/// Represents watch folder transcription request data.
/// </summary>
/// <param name="FilePath">File path supplied to the member.</param>
public sealed record WatchFolderTranscriptionRequest(string FilePath);

/// <summary>
/// Represents watch folder transcription result data.
/// </summary>
/// <param name="Text">Text supplied to the member.</param>
/// <param name="DetectedLanguage">Detected language supplied to the member.</param>
/// <param name="Duration">Duration supplied to the member.</param>
/// <param name="ProcessingTime">Processing time supplied to the member.</param>
/// <param name="Segments">Segments supplied to the member.</param>
/// <param name="EngineId">Engine id supplied to the member.</param>
/// <param name="ModelId">Model id supplied to the member.</param>
public sealed record WatchFolderTranscriptionResult(
    string Text,
    string? DetectedLanguage,
    double Duration,
    double ProcessingTime,
    IReadOnlyList<TranscriptionSegment> Segments,
    string? EngineId,
    string? ModelId);

/// <summary>
/// Represents watch folder export artifact data.
/// </summary>
/// <param name="FileExtension">File extension supplied to the member.</param>
/// <param name="Content">Content supplied to the member.</param>
public sealed record WatchFolderExportArtifact(string FileExtension, string Content);

/// <summary>
/// Represents watch folder history item data.
/// </summary>
public sealed record WatchFolderHistoryItem
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the file name value.
    /// </summary>
    public required string FileName { get; init; }
    /// <summary>
    /// Gets or sets the processed at utc value.
    /// </summary>
    public required DateTime ProcessedAtUtc { get; init; }
    /// <summary>
    /// Gets or sets the output path value.
    /// </summary>
    public required string OutputPath { get; init; }
    /// <summary>
    /// Gets or sets the success value.
    /// </summary>
    public required bool Success { get; init; }
    /// <summary>
    /// Gets or sets the error message value.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Provides watch folder output formats behavior.
/// </summary>
public static class WatchFolderOutputFormats
{
    /// <summary>
    /// Parses the supplied value into the expected representation.
    /// </summary>
    public static WatchFolderOutputFormat Parse(string? storedValue) =>
        string.Equals(storedValue, "txt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.PlainText :
        string.Equals(storedValue, "srt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.Srt :
        string.Equals(storedValue, "vtt", StringComparison.OrdinalIgnoreCase) ? WatchFolderOutputFormat.Vtt :
        WatchFolderOutputFormat.Markdown;

    /// <summary>
    /// Converts to stored value.
    /// </summary>
    public static string ToStoredValue(WatchFolderOutputFormat format) => format switch
    {
        WatchFolderOutputFormat.PlainText => "txt",
        WatchFolderOutputFormat.Srt => "srt",
        WatchFolderOutputFormat.Vtt => "vtt",
        _ => "md"
    };

    /// <summary>
    /// Converts to file extension.
    /// </summary>
    public static string ToFileExtension(WatchFolderOutputFormat format) => ToStoredValue(format);
}
