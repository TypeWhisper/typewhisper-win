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

    /// <summary>Removes the default filler words from <paramref name="text"/>.</summary>
    public static string Remove(string text) => Remove(text, DefaultFillerWords);

    /// <summary>Removes the given filler words from <paramref name="text"/>.</summary>
    public static string Remove(string text, IReadOnlyList<string> words)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = NormalizeWords(words);
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
        var lines = text.Split('\n');
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

        if (MatcherCache.Count >= MatcherCacheLimit)
            MatcherCache.Clear();

        return MatcherCache.GetOrAdd(key, _ => new FillerWordMatcher(normalizedWords));
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
