using System.Text.Json.Serialization;

namespace TypeWhisper.Core.Models;

/// <summary>
/// Determines how visible spoken formatting commands are handled.
/// </summary>
public enum SpokenFormattingStrategy
{
    /// <summary>Uses the transcription engine output without local formatting.</summary>
    NativeOnly,
    /// <summary>Applies local formatting only when a visible command is present.</summary>
    Automatic,
    /// <summary>Always applies local formatting and spacing normalization.</summary>
    FallbackOnly
}

/// <summary>
/// Determines whether spacing normalization only follows a visible command or always runs.
/// </summary>
public enum SpokenFormattingApplicationMode
{
    /// <summary>Normalizes text only after a visible spoken command was replaced.</summary>
    SelectiveFallback,
    /// <summary>Always normalizes supported text.</summary>
    FullFallback
}

/// <summary>
/// Describes the verification state of an engine, model, and language profile.
/// </summary>
public enum SpokenFormattingVerificationState
{
    /// <summary>The profile has not been verified.</summary>
    Unknown,
    /// <summary>The engine vendor documents known native behavior for this profile.</summary>
    VendorHint,
    /// <summary>The user verified that native formatting works.</summary>
    UserVerifiedGood,
    /// <summary>The user verified that local fallback formatting is needed.</summary>
    UserVerifiedBad
}

/// <summary>
/// Categorizes spoken formatting rules.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SpokenFormattingRuleCategory>))]
public enum SpokenFormattingRuleCategory
{
    /// <summary>A punctuation mark.</summary>
    Punctuation,
    /// <summary>An opening or closing bracket.</summary>
    Brackets,
    /// <summary>A quotation mark.</summary>
    Quotes,
    /// <summary>A line, paragraph, or tab command.</summary>
    Structural
}

/// <summary>
/// Represents one visible spoken command and its output text.
/// </summary>
public sealed record SpokenFormattingRule
{
    /// <summary>Gets the visible phrase emitted by the transcription engine.</summary>
    public string Phrase { get; init; } = "";
    /// <summary>Gets the replacement text.</summary>
    public string Replacement { get; init; } = "";
    /// <summary>Gets the rule category.</summary>
    public SpokenFormattingRuleCategory Category { get; init; }
}

/// <summary>
/// Represents one guided verification phrase.
/// </summary>
public sealed record SpokenFormattingVerificationScenario
{
    /// <summary>Gets the phrase the user should dictate.</summary>
    public string Spoken { get; init; } = "";
    /// <summary>Gets the expected formatted output.</summary>
    public string Expected { get; init; } = "";
}

/// <summary>
/// Contains spoken formatting rules and verification phrases for one language.
/// </summary>
public sealed record SpokenFormattingRuleSet
{
    /// <summary>Gets the normalized primary language code.</summary>
    public string Language { get; init; } = "";
    /// <summary>Gets the formatting rules.</summary>
    public IReadOnlyList<SpokenFormattingRule> Rules { get; init; } = [];
    /// <summary>Gets the guided verification scenarios.</summary>
    public IReadOnlyList<SpokenFormattingVerificationScenario> VerificationScenarios { get; init; } = [];
}

/// <summary>
/// Stores a user override for one transcription engine, model, and language.
/// Raw strings keep settings forward compatible with future values.
/// </summary>
public sealed record DictationSpokenFormattingProfile
{
    /// <summary>Gets the transcription engine provider identifier.</summary>
    public string EngineId { get; init; } = "";
    /// <summary>Gets the raw model identifier.</summary>
    public string? ModelId { get; init; }
    /// <summary>Gets the normalized primary language code.</summary>
    public string LanguageCode { get; init; } = "";
    /// <summary>Gets the forward-compatible persisted strategy override.</summary>
    public string? StrategyOverrideRaw { get; init; }
    /// <summary>Gets the forward-compatible persisted verification state.</summary>
    public string VerificationStateRaw { get; init; } = "unknown";
    /// <summary>Gets the most recent user verification timestamp.</summary>
    public DateTime? LastVerifiedAt { get; init; }

    /// <summary>Gets the parsed strategy override, or <see langword="null"/> for unknown values.</summary>
    [JsonIgnore]
    public SpokenFormattingStrategy? StrategyOverride =>
        SpokenFormattingStrategyValues.TryParse(StrategyOverrideRaw, out var strategy)
            ? strategy
            : null;

    /// <summary>Gets the parsed verification state.</summary>
    [JsonIgnore]
    public SpokenFormattingVerificationState VerificationState =>
        SpokenFormattingVerificationStateValues.Parse(VerificationStateRaw);

    /// <summary>Gets the composite profile key.</summary>
    [JsonIgnore]
    public string Key => MakeKey(EngineId, ModelId, LanguageCode);

    /// <summary>Creates a composite key for an engine, model, and language.</summary>
    public static string MakeKey(string engineId, string? modelId, string languageCode) =>
        $"{engineId}::{modelId ?? "__default__"}::{languageCode}";
}

/// <summary>
/// Contains the effective formatting strategy and normalized profile context.
/// </summary>
public sealed record ResolvedSpokenFormattingStrategy(
    string LanguageCode,
    SpokenFormattingStrategy Strategy,
    DictationSpokenFormattingProfile Profile);

/// <summary>
/// Converts persisted spoken formatting strategy values.
/// </summary>
public static class SpokenFormattingStrategyValues
{
    /// <summary>Parses a forward-compatible persisted strategy value.</summary>
    public static bool TryParse(string? rawValue, out SpokenFormattingStrategy strategy)
    {
        switch (rawValue?.Trim().ToLowerInvariant())
        {
            case "nativeonly":
                strategy = SpokenFormattingStrategy.NativeOnly;
                return true;
            case "automatic":
                strategy = SpokenFormattingStrategy.Automatic;
                return true;
            case "fallbackonly":
                strategy = SpokenFormattingStrategy.FallbackOnly;
                return true;
            default:
                strategy = SpokenFormattingStrategy.NativeOnly;
                return false;
        }
    }

    /// <summary>Converts a strategy to its persisted value.</summary>
    public static string ToRawValue(this SpokenFormattingStrategy strategy) => strategy switch
    {
        SpokenFormattingStrategy.NativeOnly => "nativeOnly",
        SpokenFormattingStrategy.FallbackOnly => "fallbackOnly",
        _ => "automatic"
    };
}

/// <summary>
/// Converts persisted spoken formatting verification values.
/// </summary>
public static class SpokenFormattingVerificationStateValues
{
    /// <summary>Determines whether a persisted verification value is currently known.</summary>
    public static bool IsKnown(string? rawValue) => rawValue?.Trim().ToLowerInvariant() is
        "unknown" or "vendorhint" or "userverifiedgood" or "userverifiedbad";

    /// <summary>Parses a forward-compatible persisted verification value.</summary>
    public static SpokenFormattingVerificationState Parse(string? rawValue) =>
        rawValue?.Trim().ToLowerInvariant() switch
        {
            "vendorhint" => SpokenFormattingVerificationState.VendorHint,
            "userverifiedgood" => SpokenFormattingVerificationState.UserVerifiedGood,
            "userverifiedbad" => SpokenFormattingVerificationState.UserVerifiedBad,
            _ => SpokenFormattingVerificationState.Unknown
        };

    /// <summary>Converts a verification state to its persisted value.</summary>
    public static string ToRawValue(this SpokenFormattingVerificationState state) => state switch
    {
        SpokenFormattingVerificationState.VendorHint => "vendorHint",
        SpokenFormattingVerificationState.UserVerifiedGood => "userVerifiedGood",
        SpokenFormattingVerificationState.UserVerifiedBad => "userVerifiedBad",
        _ => "unknown"
    };
}

/// <summary>
/// Normalizes regional language identifiers to their primary language code.
/// </summary>
public static class SpokenFormattingLanguageNormalizer
{
    /// <summary>Normalizes a language identifier to its lower-case primary code.</summary>
    public static string? Normalize(string? languageCode)
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
