using System.Collections.Concurrent;
using System.Text;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Removes configured filler words from transcribed text.
/// Latin-script and Japanese-script words use separate matching rules because
/// Japanese text has no whitespace word boundaries.
/// </summary>
public static class FillerWordFilter
{
    private const int MatcherCacheLimit = 8;

    private static readonly ConcurrentDictionary<string, FillerWordMatcher> MatcherCache = new(StringComparer.Ordinal);
    private static readonly object MatcherCacheGate = new();

    private static readonly char[] WordSeparators = [',', ';'];

    /// <summary>Filler words applied when the user has not customized the list.</summary>
    public static IReadOnlyList<string> DefaultFillerWords { get; } =
    [
        "ah",
        "ahh",
        "eh",
        "ehm",
        "hm",
        "hmm",
        "uh",
        "uhh",
        "um",
        "umm",
        "äh",
        "ähm",
        "えっと",
        "えーっと",
        "ええと",
        "えーと",
        "えと",
        "なんか",
        "まぁ",
        "まあ",
        "あのー",
        "あのぉ",
        "そのー",
        "そのぉ",
        "うーん",
        "うーむ"
    ];

    /// <summary>
    /// Filler words that are real words elsewhere, keyed to the languages in which they
    /// are only filler: "um" is German for "at" and Portuguese for "a", "eh" is German for
    /// "anyway". They are removed only when the text is known to be in one of these languages.
    /// </summary>
    private static readonly Dictionary<string, string[]> LanguageBoundFillerWords = new(StringComparer.Ordinal)
    {
        ["um"] = ["en"],
        ["ah"] = ["en", "de"],
        ["eh"] = ["en"]
    };

    /// <summary>Removes the default filler words from <paramref name="text"/>.</summary>
    /// <param name="text">The text to clean.</param>
    /// <param name="language">The language of <paramref name="text"/>, or null when unknown.</param>
    public static string Remove(string text, string? language = null) => Remove(text, DefaultFillerWords, language);

    /// <summary>Removes the given filler words from <paramref name="text"/>.</summary>
    /// <param name="text">The text to clean.</param>
    /// <param name="words">The filler words to remove.</param>
    /// <param name="language">
    /// The language reported for <paramref name="text"/>, or null when unknown. A language the
    /// text itself shows reliably takes precedence, because a translated dictation reports its
    /// source language while the text is English.
    /// </param>
    public static string Remove(string text, IReadOnlyList<string> words, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = WordsForLanguage(NormalizeWords(words), language, text);
        if (normalized.Count == 0)
            return text;

        return GetMatcher(normalized).Apply(text);
    }

    /// <summary>
    /// Splits user-entered text into filler words. Newlines, commas and semicolons
    /// all separate entries.
    /// </summary>
    public static IReadOnlyList<string> NormalizeWords(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>(lines.Length);
        foreach (var line in lines)
            parts.AddRange(line.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));

        return NormalizeWords(parts);
    }

    /// <summary>
    /// Trims, lower-cases and de-duplicates the given words, ordering longest first so
    /// that longer fillers win over words that are a prefix of them.
    /// </summary>
    public static IReadOnlyList<string> NormalizeWords(IReadOnlyList<string> words)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(words.Count);

        foreach (var word in words)
        {
            var cleaned = word.Trim().ToLowerInvariant();
            if (cleaned.Length == 0 || !seen.Add(cleaned))
                continue;

            normalized.Add(cleaned);
        }

        normalized.Sort(static (left, right) =>
            left.Length != right.Length
                ? right.Length - left.Length
                : string.CompareOrdinal(left, right));

        return normalized;
    }

    /// <summary>
    /// Drops language-bound filler words unless a reliable guess from <paramref name="text"/>,
    /// or failing that <paramref name="language"/>, is one of their languages.
    /// </summary>
    private static IReadOnlyList<string> WordsForLanguage(IReadOnlyList<string> words, string? language, string text)
    {
        if (!words.Any(LanguageBoundFillerWords.ContainsKey))
            return words;

        var textLanguage = TextLanguageDetector.Detect(text) ?? NormalizeLanguage(language);

        return words
            .Where(word => !LanguageBoundFillerWords.TryGetValue(word, out var languages)
                || (textLanguage is not null && languages.Contains(textLanguage, StringComparer.Ordinal)))
            .ToList();
    }

    /// <summary>Reduces a language tag such as "de-DE" to its lower-case primary subtag.</summary>
    private static string? NormalizeLanguage(string? language)
    {
        var primary = language?.Trim().Split('-', '_')[0].ToLowerInvariant();

        return string.IsNullOrEmpty(primary) || primary == "auto" ? null : primary;
    }

    /// <summary>Returns whether the word contains kana or CJK ideographs.</summary>
    internal static bool ContainsJapaneseScript(string word)
    {
        foreach (var c in word)
        {
            int code = c;
            var isJapanese = code
                is (>= 0x3040 and <= 0x309F)  // Hiragana
                or (>= 0x30A0 and <= 0x30FF)  // Katakana
                or (>= 0x31F0 and <= 0x31FF)  // Katakana phonetic extensions
                or (>= 0x3400 and <= 0x4DBF)  // CJK unified ideographs extension A
                or (>= 0x4E00 and <= 0x9FFF); // CJK unified ideographs

            if (isJapanese)
                return true;
        }

        return false;
    }

    private static FillerWordMatcher GetMatcher(IReadOnlyList<string> normalizedWords)
    {
        var key = BuildCacheKey(normalizedWords);
        if (MatcherCache.TryGetValue(key, out var cached))
            return cached;

        lock (MatcherCacheGate)
        {
            if (MatcherCache.TryGetValue(key, out cached))
                return cached;

            if (MatcherCache.Count >= MatcherCacheLimit)
                MatcherCache.Clear();

            var matcher = new FillerWordMatcher(normalizedWords);
            MatcherCache[key] = matcher;
            return matcher;
        }
    }

    /// <summary>
    /// Builds a cache key that no other word list can produce. Entries carry their own
    /// length because a filler word may contain spaces, which would make a plain
    /// separator ambiguous.
    /// </summary>
    private static string BuildCacheKey(IReadOnlyList<string> words)
    {
        var key = new StringBuilder();
        foreach (var word in words)
            key.Append(word.Length).Append(':').Append(word);

        return key.ToString();
    }
}
