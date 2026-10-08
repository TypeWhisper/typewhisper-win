namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Result of a transcription operation from a plugin engine.
/// </summary>
/// <param name="Text">The transcribed text.</param>
/// <param name="DetectedLanguage">ISO language code detected in the audio, or null.</param>
/// <param name="DurationSeconds">Duration of the audio in seconds.</param>
/// <param name="NoSpeechProbability">Provider-reported probability that the audio does not contain speech, or null when unavailable.</param>
public sealed record PluginTranscriptionResult(
    string Text, string? DetectedLanguage, double DurationSeconds,
    float? NoSpeechProbability = null)
{
    /// <summary>
    /// Gets or sets the segments value.
    /// </summary>
    public IReadOnlyList<PluginTranscriptionSegment> Segments { get; init; } = [];

    /// <summary>Token intervals supplied by local engines for acoustic vocabulary refinement.</summary>
    public IReadOnlyList<VocabularyTokenTiming> TokenTimings { get; init; } = [];

    /// <summary>
    /// Backward-compatible constructor for plugins compiled against SDK &lt; 1.1.
    /// </summary>
    public PluginTranscriptionResult(string text, string? detectedLanguage, double durationSeconds)
        : this(text, detectedLanguage, durationSeconds, null) { }
}

/// <summary>
/// Represents plugin transcription segment data.
/// </summary>
/// <param name="Text">Text supplied to the member.</param>
/// <param name="Start">Start supplied to the member.</param>
/// <param name="End">End supplied to the member.</param>
public sealed record PluginTranscriptionSegment(string Text, double Start, double End)
{
    /// <summary>
    /// Provider-reported probability that this segment contains no speech, or null when the response did not
    /// include one. The result-level value is the minimum across segments; the per-segment value lets plugins
    /// judge a single trailing segment, such as a hallucinated "Thank you." after silence.
    /// </summary>
    public float? NoSpeechProbability { get; init; }
}
