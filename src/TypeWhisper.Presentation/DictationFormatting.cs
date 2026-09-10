using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Presentation;

/// <summary>Connects immutable dictation preferences to the existing Core spoken formatting profiles and rules.</summary>
public static class DictationFormatting
{
    private static readonly SpokenFormattingRulesLoader Rules = new();
    private static readonly SpokenFormattingService Service = new(Rules);

    /// <summary>Languages for which local spoken-command rules are available.</summary>
    public static IReadOnlyCollection<string> SupportedLanguages => Rules.SupportedLanguages;

    /// <summary>Resolves a captured engine/model/language profile; native translation uses the English output profile.</summary>
    public static ResolvedSpokenFormattingStrategy? Resolve(DictationTextPreferences preferences,
        string? engineId, string? modelId, string? configuredLanguage, string? detectedLanguage,
        TranscriptionTask task = TranscriptionTask.Transcribe)
    {
        var settings = new SnapshotSettings(preferences.SpokenFormattingProfiles);
        var resolver = new SpokenFormattingStrategyResolver(new SpokenFormattingProfileStore(settings), Rules);
        var configured = task == TranscriptionTask.Translate ? "en" : SpokenFormattingLanguageNormalizer.Normalize(configuredLanguage);
        return resolver.Resolve(engineId, modelId, configured is null ? [] : [configured],
            task == TranscriptionTask.Translate ? "en" : detectedLanguage);
    }

    /// <summary>Applies the resolved Core strategy; unsupported contexts and native-only profiles preserve text.</summary>
    public static string Apply(string text, ResolvedSpokenFormattingStrategy? resolved) => resolved?.Strategy switch
    {
        SpokenFormattingStrategy.Automatic => Service.Normalize(text, resolved.LanguageCode, SpokenFormattingApplicationMode.SelectiveFallback),
        SpokenFormattingStrategy.FallbackOnly => Service.Normalize(text, resolved.LanguageCode, SpokenFormattingApplicationMode.FullFallback),
        _ => text
    };

    /// <summary>Creates an updated profile choice while retaining unrelated profiles and existing verification metadata. A null strategy restores engine defaults.</summary>
    public static DictationTextPreferences WithProfile(DictationTextPreferences preferences, string engineId,
        string? modelId, string language, SpokenFormattingStrategy? strategy)
    {
        var normalizedLanguage = SpokenFormattingLanguageNormalizer.Normalize(language);
        if (string.IsNullOrWhiteSpace(engineId) || normalizedLanguage is null || !Rules.Supports(normalizedLanguage) ||
            strategy is { } value && !Enum.IsDefined(value)) throw new ArgumentException("Choose a known engine and a supported formatting language and strategy.");
        var engine = engineId.Trim();
        var model = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var key = DictationSpokenFormattingProfile.MakeKey(engine, model, normalizedLanguage);
        var existing = preferences.SpokenFormattingProfiles.FirstOrDefault(profile => profile.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var updated = (existing ?? new DictationSpokenFormattingProfile()) with
        {
            EngineId = engine, ModelId = model, LanguageCode = normalizedLanguage, StrategyOverrideRaw = strategy?.ToRawValue()
        };
        return preferences with
        {
            SpokenFormattingProfiles = Array.AsReadOnly(preferences.SpokenFormattingProfiles.Where(profile => !profile.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Append(updated).ToArray())
        };
    }

    private sealed class SnapshotSettings(IReadOnlyList<DictationSpokenFormattingProfile> profiles) : ISettingsService
    {
        public AppSettings Current { get; } = new() { SpokenFormattingProfiles = profiles };
        public event Action<AppSettings>? SettingsChanged { add { } remove { } }
        public AppSettings Load() => Current;
        public void Save(AppSettings settings) => throw new NotSupportedException("The formatting resolver reads a recording snapshot.");
    }
}
