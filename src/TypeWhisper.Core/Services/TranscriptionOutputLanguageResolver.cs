using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.Core.Services;

internal static class TranscriptionOutputLanguageResolver
{
    public static bool IsOutputLanguage(
        string expectedLanguage,
        TranscriptionTask transcriptionTask,
        string? detectedLanguage,
        string? configuredLanguage,
        IReadOnlyList<string> configuredLanguageCandidates,
        string? translationTarget = null)
    {
        var target = NormalizeLanguageCode(translationTarget);
        if (target is not null)
            return target == expectedLanguage;

        if (transcriptionTask == TranscriptionTask.Translate)
            return expectedLanguage == "en";

        var detected = NormalizeLanguageCode(detectedLanguage);
        if (detected is not null)
            return detected == expectedLanguage;

        var configured = NormalizeLanguageCode(configuredLanguage);
        if (configured is not null)
            return configured == expectedLanguage;

        var firstCandidate = configuredLanguageCandidates
            .Select(NormalizeLanguageCode)
            .FirstOrDefault(static language => language is not null);

        return firstCandidate == expectedLanguage;
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        var value = languageCode?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var primary = value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(primary) ? null : primary.ToLowerInvariant();
    }
}
