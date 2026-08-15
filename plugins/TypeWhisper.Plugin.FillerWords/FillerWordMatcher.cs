using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Compiled matching rules for one filler word list.
/// </summary>
internal sealed class FillerWordMatcher
{
    private static readonly Regex CollapsedSpacesPattern = new(@"(?<=[^\s]) {2,}(?=[^\s])", RegexOptions.Compiled);
    private static readonly Regex LeadingSpacesPattern = new(@"^ +", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex TrailingSpacesPattern = new(@" +$", RegexOptions.Compiled);

    private static readonly char[] HorizontalWhitespace = [' ', '\t'];

    private readonly Regex? _latin;
    private readonly Regex? _japanese;

    internal FillerWordMatcher(IReadOnlyList<string> normalizedWords)
    {
        var latinWords = normalizedWords.Where(static word => !FillerWordFilter.ContainsJapaneseScript(word)).ToList();
        var japaneseWords = normalizedWords.Where(FillerWordFilter.ContainsJapaneseScript).ToList();

        _latin = latinWords.Count == 0 ? null : BuildLatinPattern(latinWords);
        _japanese = japaneseWords.Count == 0 ? null : BuildJapanesePattern(japaneseWords);
    }

    /// <summary>Strips the matcher's filler words from <paramref name="text"/>.</summary>
    internal string Apply(string text)
    {
        var result = ReplaceAndNormalize(text, _latin, " ");
        return ReplaceAndNormalize(result, _japanese, "$1");
    }

    private static Regex BuildLatinPattern(IReadOnlyList<string> words)
    {
        var alternation = string.Join('|', words.Select(Regex.Escape));
        var pattern = @"(?<![\p{L}\p{N}_])[,.!?]?[ \t]*(?:" + alternation + @")(?![\p{L}\p{N}_])[ \t]*[,.!?]?";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static Regex BuildJapanesePattern(IReadOnlyList<string> words)
    {
        var alternation = string.Join('|', words.Select(static word =>
        {
            var escaped = Regex.Escape(word);

            // "まあ"/"まぁ" must not swallow the leading half of a longer drawl such as "まあまあ".
            return word is "まあ" or "まぁ" ? escaped + @"(?!ま[あぁ])" : escaped;
        }));

        const string Boundary = @"(^|[\s、。,.!?！？])";
        const string TrailingSeparator = @"(?:[ \t]*[、,][ \t]*|[ \t]+)?";
        var pattern = Boundary + @"[ \t]*(?:" + alternation + ")" + TrailingSeparator;

        return new Regex(pattern, RegexOptions.Compiled);
    }

    private static string ReplaceAndNormalize(string text, Regex? pattern, string replacement)
    {
        if (pattern is null)
            return text;

        var stripped = pattern.Replace(text, replacement);
        return string.Equals(stripped, text, StringComparison.Ordinal)
            ? text
            : NormalizeWhitespace(stripped, text);
    }

    private static string NormalizeWhitespace(string text, string original)
    {
        var result = CollapsedSpacesPattern.Replace(text, " ");
        result = LeadingSpacesPattern.Replace(result, string.Empty);
        result = TrailingSpacesPattern.Replace(result, string.Empty);
        result = result.Trim(HorizontalWhitespace);

        // Leading spaces in the input separate this text from whatever the host has
        // already typed, so restore them once the removal artifacts are gone.
        var prefix = LeadingHorizontalWhitespace(original);
        return prefix.Length == 0 || result.Length == 0 ? result : prefix + result;
    }

    private static string LeadingHorizontalWhitespace(string text)
    {
        var length = 0;
        while (length < text.Length && (text[length] == ' ' || text[length] == '\t'))
            length++;

        return text[..length];
    }
}
