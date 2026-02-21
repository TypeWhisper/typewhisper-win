namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Result of a transcription operation from a plugin engine.
/// </summary>
/// <param name="Text">The transcribed text.</param>
/// <param name="DetectedLanguage">ISO language code detected in the audio, or null.</param>
/// <param name="DurationSeconds">Duration of the audio in seconds.</param>
public sealed record PluginTranscriptionResult(string Text, string? DetectedLanguage, double DurationSeconds);
