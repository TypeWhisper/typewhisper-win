using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Applies safe regional spelling rules to final German output.
/// </summary>
public static class GermanOutputNormalizationService
{
    /// <summary>
    /// Applies the selected German output variant to text when the final language is German.
    /// </summary>
    public static string NormalizeText(
        string text,
        GermanOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? detectedLanguage = null,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null,
        string? translationTarget = null)
    {
        if (variant != GermanOutputVariant.Switzerland
            || string.IsNullOrEmpty(text)
            || !TranscriptionOutputLanguageResolver.IsOutputLanguage(
                "de",
                transcriptionTask,
                detectedLanguage,
                configuredLanguage,
                configuredLanguageCandidates ?? [],
                translationTarget))
        {
            return text;
        }

        return text
            .Replace("ẞ", "SS", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);
    }

    /// <summary>
    /// Applies the selected German output variant to transcription text and segments.
    /// </summary>
    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        GermanOutputVariant variant,
        TranscriptionTask transcriptionTask = TranscriptionTask.Transcribe,
        string? configuredLanguage = null,
        IReadOnlyList<string>? configuredLanguageCandidates = null)
    {
        var candidates = configuredLanguageCandidates ?? [];
        return result with
        {
            Text = NormalizeText(
                result.Text,
                variant,
                transcriptionTask,
                result.DetectedLanguage,
                configuredLanguage,
                candidates),
            Segments = result.Segments
                .Select(segment => segment with
                {
                    Text = NormalizeText(
                        segment.Text,
                        variant,
                        transcriptionTask,
                        result.DetectedLanguage,
                        configuredLanguage,
                        candidates)
                })
                .ToList()
        };
    }

}
