using System.Globalization;

namespace TypeWhisper.Presentation;

/// <summary>Normalizes provider language metadata without guessing a language from transcript text.</summary>
public static class DictationProvenance
{
    private static readonly CultureInfo[] Languages = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(culture => culture.Name.Length > 0).ToArray();

    /// <summary>Prefers a recognized provider language, then an explicitly configured language; automatic remains unknown.</summary>
    public static string? ResolveLanguage(string? detected, string? configured) => Normalize(detected) ?? Normalize(configured);

    private static string? Normalize(string? value)
    {
        var language = value?.Trim();
        if (string.IsNullOrEmpty(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var known = Languages.FirstOrDefault(culture =>
            culture.Name.Equals(language, StringComparison.OrdinalIgnoreCase) ||
            culture.EnglishName.Equals(language, StringComparison.OrdinalIgnoreCase) ||
            culture.ThreeLetterISOLanguageName.Equals(language, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known.TwoLetterISOLanguageName;
        // A regional BCP-47 code is meaningful only if its base language is recognized.
        var separator = language.IndexOf('-');
        return separator > 0 ? Normalize(language[..separator]) : null;
    }
}
