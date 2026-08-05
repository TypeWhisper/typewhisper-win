using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>
/// Resolves the effective spoken formatting strategy for a dictation context.
/// </summary>
public sealed class SpokenFormattingStrategyResolver
{
    private readonly SpokenFormattingProfileStore _profileStore;
    private readonly SpokenFormattingRulesLoader _rulesLoader;

    /// <summary>Initializes a new strategy resolver.</summary>
    public SpokenFormattingStrategyResolver(
        SpokenFormattingProfileStore profileStore,
        SpokenFormattingRulesLoader rulesLoader)
    {
        _profileStore = profileStore;
        _rulesLoader = rulesLoader;
    }

    /// <summary>Resolves the effective profile for a captured dictation context.</summary>
    public ResolvedSpokenFormattingStrategy? Resolve(
        string? engineId,
        string? modelId,
        IReadOnlyList<string>? configuredLanguageCandidates,
        string? detectedLanguage)
    {
        var normalizedEngine = engineId?.Trim();
        if (string.IsNullOrEmpty(normalizedEngine))
            return null;

        var languageCode = ResolveLanguage(configuredLanguageCandidates, detectedLanguage);
        if (languageCode is null)
            return null;

        var normalizedModel = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var storedProfile = _profileStore.Profile(normalizedEngine, normalizedModel, languageCode);
        var profile = storedProfile ?? new DictationSpokenFormattingProfile
        {
            EngineId = normalizedEngine,
            ModelId = normalizedModel,
            LanguageCode = languageCode,
            VerificationStateRaw = DefaultVerificationState(normalizedModel, languageCode).ToRawValue()
        };

        return new ResolvedSpokenFormattingStrategy(
            languageCode,
            profile.StrategyOverride ?? SpokenFormattingStrategy.NativeOnly,
            profile);
    }

    private string? ResolveLanguage(
        IReadOnlyList<string>? configuredLanguageCandidates,
        string? detectedLanguage)
    {
        var candidates = (configuredLanguageCandidates ?? [])
            .Select(SpokenFormattingLanguageNormalizer.Normalize)
            .Where(static language => language is not null)
            .Select(static language => language!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var detected = SpokenFormattingLanguageNormalizer.Normalize(detectedLanguage);
        var detectedSupported = detected is not null && _rulesLoader.Supports(detected);

        if (candidates.Count == 1)
            return _rulesLoader.Supports(candidates[0]) ? candidates[0] : null;
        if (detectedSupported)
            return detected;
        return candidates.FirstOrDefault(_rulesLoader.Supports);
    }

    private static SpokenFormattingVerificationState DefaultVerificationState(
        string? modelId,
        string languageCode) =>
        string.Equals(modelId, "parakeet-tdt-0.6b", StringComparison.OrdinalIgnoreCase)
        && string.Equals(languageCode, "de", StringComparison.OrdinalIgnoreCase)
            ? SpokenFormattingVerificationState.VendorHint
            : SpokenFormattingVerificationState.Unknown;
}
