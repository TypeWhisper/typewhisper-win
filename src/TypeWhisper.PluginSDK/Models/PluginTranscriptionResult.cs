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

    /// <summary>
    /// Backward-compatible constructor for plugins compiled against SDK &lt; 1.1.
    /// </summary>
    public PluginTranscriptionResult(string text, string detectedLanguage, double durationSeconds)
        : this(text, detectedLanguage, durationSeconds, null) { }
}

/// <summary>
/// Represents plugin transcription segment data.
/// </summary>
/// <param name="Text">Text supplied to the member.</param>
/// <param name="Start">Start supplied to the member.</param>
/// <param name="End">End supplied to the member.</param>
public sealed record PluginTranscriptionSegment(string Text, double Start, double End);
