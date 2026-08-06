using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Describes whether a transcription record completed successfully.
/// </summary>
public enum TranscriptionRecordStatus
{
    /// <summary>
    /// The complete dictation pipeline succeeded.
    /// </summary>
    Succeeded = 0,
    /// <summary>
    /// Speech-to-text succeeded, but workflow post-processing failed.
    /// </summary>
    WorkflowPostProcessingFailed
}

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
    /// Gets or sets the workflow id used for post-processing.
    /// </summary>
    public string? WorkflowId { get; init; }
    /// <summary>
    /// Gets or sets the record status.
    /// </summary>
    public TranscriptionRecordStatus Status { get; init; } = TranscriptionRecordStatus.Succeeded;
    /// <summary>
    /// Gets or sets the sanitized workflow failure message.
    /// </summary>
    public string? WorkflowFailureMessage { get; init; }
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
    /// Gets or sets the separate recovery audio file name.
    /// </summary>
    public string? RecoveryAudioFileName { get; init; }
    /// <summary>
    /// Gets or sets the transcription task that was used.
    /// </summary>
    public string? TranscriptionTaskUsed { get; init; }
    /// <summary>
    /// Gets or sets whether the configured transcription fallback was used.
    /// </summary>
    public bool UsedTranscriptionFallback { get; init; }
    /// <summary>
    /// Gets or sets the created at value.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Returns final text when available, otherwise the preserved raw text.
    /// </summary>
    [JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(FinalText) ? RawText : FinalText;
    /// <summary>
    /// Returns the word count based on the displayed text.
    /// </summary>
    [JsonIgnore]
    public int WordCount => DisplayText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    /// <summary>
    /// Returns the preview.
    /// </summary>
    [JsonIgnore]
    public string Preview => DisplayText.Length > 100
        ? string.Concat(DisplayText.AsSpan(0, 100), "...")
        : DisplayText;
}
