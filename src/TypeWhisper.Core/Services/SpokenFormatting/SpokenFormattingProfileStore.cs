using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.SpokenFormatting;

/// <summary>
/// Persists spoken formatting profile overrides in application settings.
/// </summary>
public sealed class SpokenFormattingProfileStore
{
    private readonly ISettingsService _settings;

    /// <summary>Initializes a new profile store.</summary>
    public SpokenFormattingProfileStore(ISettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>Gets the currently persisted profiles.</summary>
    public IReadOnlyList<DictationSpokenFormattingProfile> Profiles =>
        _settings.Current.SpokenFormattingProfiles;

    /// <summary>Gets the profile for an engine, model, and language.</summary>
    public DictationSpokenFormattingProfile? Profile(
        string engineId,
        string? modelId,
        string languageCode)
    {
        var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        var normalizedEngine = engineId.Trim();
        var normalizedModel = NormalizeOptional(modelId);
        if (normalizedLanguage is null || normalizedEngine.Length == 0)
            return null;

        var key = DictationSpokenFormattingProfile.MakeKey(
            normalizedEngine,
            normalizedModel,
            normalizedLanguage);
        return Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Saves a user strategy choice and optional verification result.</summary>
    public void SaveUserOverride(
        string engineId,
        string? modelId,
        string languageCode,
        SpokenFormattingStrategy strategy,
        SpokenFormattingVerificationState? verificationState = null,
        bool updateVerificationDate = false)
    {
        var normalizedEngine = engineId.Trim();
        var normalizedModel = NormalizeOptional(modelId);
        var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(languageCode);
        if (normalizedEngine.Length == 0 || normalizedLanguage is null)
            return;

        var existing = Profile(normalizedEngine, normalizedModel, normalizedLanguage);
        var updated = new DictationSpokenFormattingProfile
        {
            EngineId = normalizedEngine,
            ModelId = normalizedModel,
            LanguageCode = normalizedLanguage,
            StrategyOverrideRaw = strategy.ToRawValue(),
            VerificationStateRaw = (verificationState ?? existing?.VerificationState
                ?? SpokenFormattingVerificationState.Unknown).ToRawValue(),
            LastVerifiedAt = updateVerificationDate ? DateTime.UtcNow : existing?.LastVerifiedAt
        };

        var profiles = Profiles
            .Where(profile => !string.Equals(profile.Key, updated.Key, StringComparison.OrdinalIgnoreCase))
            .Append(updated);
        _settings.Save(_settings.Current with
        {
            SpokenFormattingProfiles = NormalizeProfiles(profiles)
        });
    }

    /// <summary>Normalizes and de-duplicates persisted profiles.</summary>
    public static IReadOnlyList<DictationSpokenFormattingProfile> NormalizeProfiles(
        IEnumerable<DictationSpokenFormattingProfile?>? profiles)
    {
        var normalized = new Dictionary<string, DictationSpokenFormattingProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles ?? [])
        {
            var engineId = profile?.EngineId?.Trim();
            var languageCode = SpokenFormattingLanguageNormalizer.Normalize(profile?.LanguageCode);
            if (string.IsNullOrEmpty(engineId) || languageCode is null)
                continue;

            var strategyRaw = SpokenFormattingStrategyValues.TryParse(profile!.StrategyOverrideRaw, out var strategy)
                ? strategy.ToRawValue()
                : NormalizeOptional(profile.StrategyOverrideRaw);
            var verificationRaw = profile.VerificationStateRaw?.Trim();
            var verificationStateRaw = SpokenFormattingVerificationStateValues.IsKnown(verificationRaw)
                ? SpokenFormattingVerificationStateValues.Parse(verificationRaw).ToRawValue()
                : string.IsNullOrEmpty(verificationRaw) ? "unknown" : verificationRaw;
            var item = profile with
            {
                EngineId = engineId,
                ModelId = NormalizeOptional(profile.ModelId),
                LanguageCode = languageCode,
                StrategyOverrideRaw = strategyRaw,
                VerificationStateRaw = verificationStateRaw
            };
            normalized[item.Key] = item;
        }

        return normalized.Values.ToList();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
