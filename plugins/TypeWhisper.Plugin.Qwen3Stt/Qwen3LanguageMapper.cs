namespace TypeWhisper.Plugin.Qwen3Stt;

public static class Qwen3LanguageMapper
{
    private static readonly IReadOnlyDictionary<string, string> NamesByCode =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh"] = "Chinese",
            ["en"] = "English",
            ["yue"] = "Cantonese",
            ["ar"] = "Arabic",
            ["de"] = "German",
            ["fr"] = "French",
            ["es"] = "Spanish",
            ["pt"] = "Portuguese",
            ["id"] = "Indonesian",
            ["it"] = "Italian",
            ["ko"] = "Korean",
            ["ru"] = "Russian",
            ["th"] = "Thai",
            ["vi"] = "Vietnamese",
            ["ja"] = "Japanese",
            ["tr"] = "Turkish",
            ["hi"] = "Hindi",
            ["ms"] = "Malay",
            ["nl"] = "Dutch",
            ["sv"] = "Swedish",
            ["da"] = "Danish",
            ["fi"] = "Finnish",
            ["pl"] = "Polish",
            ["cs"] = "Czech",
            ["fil"] = "Filipino",
            ["tl"] = "Filipino",
            ["fa"] = "Persian",
            ["el"] = "Greek",
            ["ro"] = "Romanian",
            ["hu"] = "Hungarian",
            ["mk"] = "Macedonian",
        };

    private static readonly IReadOnlyDictionary<string, string> CodesByName =
        NamesByCode
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Any(pair => pair.Key == "fil") ? "fil" : group.First().Key,
                StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SupportedLanguageCodes { get; } =
        ["zh", "en", "yue", "ar", "de", "fr", "es", "pt", "id", "it",
         "ko", "ru", "th", "vi", "ja", "tr", "hi", "ms", "nl", "sv",
         "da", "fi", "pl", "cs", "fil", "tl", "fa", "el", "ro", "hu",
         "mk"];

    public static string? ResolveLanguageName(string? isoCode)
    {
        var code = isoCode?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(code))
            return null;
        return NamesByCode.TryGetValue(code, out var name) ? name : null;
    }

    public static string? LanguageCodeForQwenLanguageName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return null;

        var names = languageName
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length != 1)
            return null;

        return CodesByName.TryGetValue(names[0], out var code) ? code : null;
    }
}
