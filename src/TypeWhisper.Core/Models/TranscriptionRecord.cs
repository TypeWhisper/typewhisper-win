namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents transcription record data.
/// </summary>
public sealed record TranscriptionRecord
{
    /// <summary>
    /// Gets or sets the id value.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// Gets or sets the timestamp value.
    /// </summary>
    public required DateTime Timestamp { get; init; }
    /// <summary>
    /// Gets or sets the raw text value.
    /// </summary>
    public required string RawText { get; init; }
    /// <summary>
    /// Gets or sets the final text value.
    /// </summary>
    public required string FinalText { get; init; }
    /// <summary>
    /// Gets or sets the app name value.
    /// </summary>
    public string? AppName { get; init; }
    /// <summary>
    /// Gets or sets the app process name value.
    /// </summary>
    public string? AppProcessName { get; init; }
    /// <summary>
    /// Gets or sets the app url value.
    /// </summary>
    public string? AppUrl { get; init; }
    /// <summary>
    /// Gets or sets the duration seconds value.
    /// </summary>
    public double DurationSeconds { get; init; }
    /// <summary>
    /// Gets or sets the language value.
    /// </summary>
    public string? Language { get; init; }
    /// <summary>
    /// Gets or sets the profile name value.
    /// </summary>
    public string? ProfileName { get; init; }
    /// <summary>
    /// Gets or sets the engine used value.
    /// </summary>
    public string EngineUsed { get; init; } = "whisper";
    /// <summary>
    /// Gets or sets the model used value.
    /// </summary>
    public string? ModelUsed { get; init; }
    /// <summary>
    /// Gets or sets the audio file name value.
    /// </summary>
    public string? AudioFileName { get; init; }
    /// <summary>
    /// Gets or sets the created at value.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Returns the word count.
    /// </summary>
    public int WordCount => FinalText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    /// <summary>
    /// Returns the preview.
    /// </summary>
    public string Preview => FinalText.Length > 100 ? string.Concat(FinalText.AsSpan(0, 100), "...") : FinalText;
}
