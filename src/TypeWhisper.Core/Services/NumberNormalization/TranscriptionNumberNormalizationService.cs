using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.NumberNormalization;

public static class TranscriptionNumberNormalizationService
{
    public static bool IsEnabled(bool globalEnabled = true, bool? normalizeNumbersOverride = null) =>
        normalizeNumbersOverride ?? globalEnabled;

    public static string NormalizeText(
        string text,
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null)
    {
        if (!IsEnabled(globalEnabled, normalizeNumbersOverride))
            return text;

        foreach (var language in NormalizationLanguages(
                     transcriptionTask,
                     detectedLanguage,
                     configuredLanguage,
                     configuredLanguageCandidates))
        {
            var normalized = NumberWordNormalizer.Normalize(text, language);
            if (!string.Equals(normalized, text, StringComparison.Ordinal))
                return normalized;
        }

        return text;
    }

    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        TranscriptionTask transcriptionTask,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null)
    {
        var languages = NormalizationLanguages(
            transcriptionTask,
            result.DetectedLanguage,
            configuredLanguage,
            configuredLanguageCandidates);

        return result with
        {
            Text = NormalizeText(
                result.Text,
                languages,
                globalEnabled,
                normalizeNumbersOverride),
            Segments = NormalizeSegments(
                result.Segments,
                languages,
                globalEnabled,
                normalizeNumbersOverride)
        };
    }

    public static IReadOnlyList<TranscriptionSegment> NormalizeSegments(
        IReadOnlyList<TranscriptionSegment> segments,
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        bool globalEnabled = true,
        bool? normalizeNumbersOverride = null)
    {
        var languages = NormalizationLanguages(
            transcriptionTask,
            detectedLanguage,
            configuredLanguage,
            configuredLanguageCandidates);

        return NormalizeSegments(segments, languages, globalEnabled, normalizeNumbersOverride);
    }

    internal static IReadOnlyList<string> NormalizationLanguages(
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates)
    {
        if (transcriptionTask == TranscriptionTask.Translate)
            return ["en"];

        return PrioritizedLanguages(
            detectedLanguage,
            [.. new[] { configuredLanguage }.Where(static language => language is not null).Select(static language => language!), .. configuredLanguageCandidates]);
    }

    private static string NormalizeText(
        string text,
        IReadOnlyList<string> languages,
        bool globalEnabled,
        bool? normalizeNumbersOverride)
    {
        if (!IsEnabled(globalEnabled, normalizeNumbersOverride))
            return text;

        foreach (var language in languages)
        {
            var normalized = NumberWordNormalizer.Normalize(text, language);
            if (!string.Equals(normalized, text, StringComparison.Ordinal))
                return normalized;
        }

        return text;
    }

    private static IReadOnlyList<TranscriptionSegment> NormalizeSegments(
        IReadOnlyList<TranscriptionSegment> segments,
        IReadOnlyList<string> languages,
        bool globalEnabled,
        bool? normalizeNumbersOverride) =>
        segments
            .Select(segment => segment with
            {
                Text = NormalizeText(segment.Text, languages, globalEnabled, normalizeNumbersOverride)
            })
            .ToList();

    private static IReadOnlyList<string> PrioritizedLanguages(string? primary, IReadOnlyList<string> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var rawLanguage in new[] { primary }.Where(static language => language is not null).Select(static language => language!).Concat(candidates))
        {
            var normalized = NumberWordNormalizer.NormalizeLanguageCode(rawLanguage);
            if (normalized is null || !seen.Add(normalized))
                continue;

            result.Add(normalized);
        }

        return result;
    }
}
