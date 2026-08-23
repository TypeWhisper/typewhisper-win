using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Compiled matching rules for one filler word list.
/// A match spans the filler word together with the punctuation and horizontal
/// whitespace attached to it, so each replacement repairs its own spacing and
/// whitespace elsewhere in the text is left untouched.
/// </summary>
internal sealed class FillerWordMatcher
{
    /// <summary>Punctuation that a spoken filler can carry, including the Unicode ellipsis.</summary>
    private const string AttachedPunctuation = @"[,.!?…]";

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
        var result = _latin is null ? text : ApplyLatin(text);

        if (_japanese is null)
            return result;

        while (_japanese.IsMatch(result))
        {
            var input = result;
            result = _japanese.Replace(input, match => JapaneseReplacement(match, input));
        }

        return result;
    }

    /// <summary>
    /// Replaces adjacent Latin matches as one run, so separators consumed between
    /// consecutive fillers do not reappear as leading, doubled or trailing whitespace.
    /// </summary>
    private string ApplyLatin(string text)
    {
        var matches = _latin!.Matches(text);
        if (matches.Count == 0)
            return text;

        var result = new StringBuilder(text.Length);
        var sourceIndex = 0;

        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            var firstMatch = matches[matchIndex];
            var runEnd = firstMatch.Index + firstMatch.Length;

            while (matchIndex + 1 < matches.Count && matches[matchIndex + 1].Index == runEnd)
            {
                matchIndex++;
                var nextMatch = matches[matchIndex];
                runEnd = nextMatch.Index + nextMatch.Length;
            }

            result.Append(text, sourceIndex, firstMatch.Index - sourceIndex);
            result.Append(LatinReplacement(firstMatch, runEnd, text));
            sourceIndex = runEnd;
        }

        result.Append(text, sourceIndex, text.Length - sourceIndex);
        return result.ToString();
    }

    private static Regex BuildLatinPattern(IReadOnlyList<string> words)
    {
        var alternation = string.Join('|', words.Select(Regex.Escape));

        // Leading punctuation is only taken as a whole run that is not itself attached to
        // surrounding text, so an ellipsis before the filler is never left half-eaten.
        var pattern =
            @"(?<lead>[ \t]*)" +
            @"(?:(?<![\p{L}\p{N}_,.!?…])" + AttachedPunctuation + @"+)?" +
            @"(?<gap>[ \t]*)" +
            @"(?<![\p{L}\p{N}_])(?:" + alternation + @")(?![\p{L}\p{N}_])" +
            AttachedPunctuation + @"*" +
            @"(?<trail>[ \t]*)";

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

    /// <summary>
    /// Rebuilds the separator for one or more adjacent removed Latin fillers from the
    /// whitespace the first match consumed, so surrounding formatting survives untouched.
    /// </summary>
    private static string LatinReplacement(Match match, int runEnd, string input)
    {
        var lead = match.Groups["lead"].Value;

        // Nothing follows on this line, so the filler's whitespace goes with it.
        if (!HasTextAfter(runEnd, input))
            return string.Empty;

        // The filler opened the line: keep only whitespace that was already there.
        if (!HasTextBefore(match, input))
            return lead;

        if (lead.Length > 0)
            return lead;

        var gap = match.Groups["gap"].Value;
        return gap.Length > 0 ? gap : match.Groups["trail"].Value;
    }

    /// <summary>
    /// Restores the boundary character the Japanese pattern matched before the filler,
    /// dropping it when it is trailing whitespace with nothing left to separate.
    /// </summary>
    private static string JapaneseReplacement(Match match, string input)
    {
        var boundary = match.Groups[1].Value;

        return boundary is " " or "\t" && !HasTextAfter(match, input) ? string.Empty : boundary;
    }

    private static bool HasTextBefore(Match match, string input) =>
        match.Index > 0 && !IsLineBreak(input[match.Index - 1]);

    private static bool HasTextAfter(Match match, string input)
    {
        var end = match.Index + match.Length;

        return HasTextAfter(end, input);
    }

    private static bool HasTextAfter(int end, string input) =>
        end < input.Length && !IsLineBreak(input[end]);

    private static bool IsLineBreak(char c) => c is '\n' or '\r';
}
