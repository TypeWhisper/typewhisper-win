using System.Globalization;

namespace TypeWhisper.Presentation;

/// <summary>
/// Spoken-language choices for a transcription model. A model that lists its languages offers only those. A model that
/// detects the language itself, such as Parakeet, offers every language, as the macOS app does: the choice then only
/// drives text processing (spoken commands, casing, number words) and is never passed to the model.
/// </summary>
public static class SpokenLanguageChoices
{
    /// <summary>Every neutral language Windows knows by a two- or three-letter code, in code order.</summary>
    public static IReadOnlyList<string> All { get; } = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Select(culture => culture.TwoLetterISOLanguageName)
        .Where(code => code != "iv" && code.Length is 2 or 3 && code.All(c => c is >= 'a' and <= 'z'))
        .Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>The languages a picker offers for a model with the given language list.</summary>
    public static IReadOnlyList<string> For(IReadOnlyList<string> supported) => supported.Count > 0 ? supported : All;

    /// <summary>
    /// Resolves a saved choice. A model with a language list falls back to English, then its first language; a model
    /// without one falls back to automatic.
    /// </summary>
    public static string Resolve(IReadOnlyList<string> supported, string? saved) => supported.Count == 0
        ? saved is not null && All.Contains(saved) ? saved : "auto"
        : supported.Contains(saved ?? "en") ? saved ?? "en" : supported[0];

    /// <summary>Whether a picker may save the language for a model with the given language list.</summary>
    public static bool CanSelect(IReadOnlyList<string> supported, string language) =>
        supported.Count == 0 ? language == "auto" || All.Contains(language) : supported.Contains(language);

    /// <summary>The language passed to the model: only one it lists itself, otherwise none, so it detects the language.</summary>
    public static string? ForEngine(IReadOnlyList<string> supported, string? language) =>
        language is not null && supported.Contains(language) ? language : null;
}
