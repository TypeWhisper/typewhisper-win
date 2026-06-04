namespace TypeWhisper.Core.Models;

/// <summary>
/// Represents transcription result data.
/// </summary>
public sealed record TranscriptionResult
{
    /// <summary>
    /// Gets or sets the text value.
    /// </summary>
    public required string Text { get; init; }
    /// <summary>
    /// Gets or sets the detected language value.
    /// </summary>
    public string? DetectedLanguage { get; init; }
    /// <summary>
    /// Gets or sets the duration value.
    /// </summary>
    public double Duration { get; init; }
    /// <summary>
    /// Gets or sets the processing time value.
    /// </summary>
    public double ProcessingTime { get; init; }
    /// <summary>
    /// Gets or sets the no speech probability value.
    /// </summary>
    public float? NoSpeechProbability { get; init; }
    /// <summary>
    /// Gets or sets the segments value.
    /// </summary>
    public IReadOnlyList<TranscriptionSegment> Segments { get; init; } = [];
}
