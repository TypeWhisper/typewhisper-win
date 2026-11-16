using System.Globalization;
using System.Text;

namespace TypeWhisper.Presentation;

/// <summary>Builds full-text queries for the History search index.</summary>
/// <remarks>
/// Only the letters and digits of the search text reach a query, every word in quotes. FTS5 operators in it, such
/// as <c>NEAR</c>, <c>NOT</c>, <c>-</c> and <c>"</c>, therefore have no effect.
/// </remarks>
public static class HistoryFullTextQuery
{
    /// <summary>From this length on, a word also finds the words that start with it.</summary>
    public const int PrefixLength = 4;

    /// <summary>A query has at most this many words, however long the search text is.</summary>
    public const int MaximumWords = 32;

    private static readonly (string Letter, string Spelling)[] GermanSpellings = [("ä", "ae"), ("ö", "oe"), ("ü", "ue"), ("ß", "ss")];

    /// <summary>
    /// The words of a text as the index splits them, lowercased and composed: letters and digits, with the accents and
    /// other combining marks that follow them.
    /// </summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var normalized = Composed(text).ToLowerInvariant();
        var words = new List<string>();
        var start = -1;
        var index = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var inWord = Rune.IsLetter(rune) || Rune.IsNumber(rune) || start >= 0 && IsMark(rune);
            if (inWord && start < 0) start = index;
            else if (!inWord && start >= 0)
            {
                words.Add(normalized[start..index]);
                start = -1;
            }
            index += rune.Utf16SequenceLength;
        }
        if (start >= 0) words.Add(normalized[start..]);
        return words;
    }

    /// <summary>A text in composed form, so "a" with a separate accent is one character as in most text. Text that cannot be normalized stays as it is.</summary>
    public static string Composed(string text)
    {
        try { return text.Normalize(NormalizationForm.FormC); }
        // A lone surrogate cannot be normalized; the tokenizer reads such text as it is too.
        catch (ArgumentException) { return text; }
    }

    private static bool IsMark(Rune rune) => Rune.GetUnicodeCategory(rune)
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>
    /// The other spelling of a German word: "mueller" for "müller", "strasse" for "straße", and back. The index folds
    /// "ü" to "u", which "ue" does not match. Null when the word has no other spelling.
    /// </summary>
    public static string? Variant(string word)
    {
        var variant = word;
        if (GermanSpellings.Any(pair => word.Contains(pair.Letter, StringComparison.Ordinal)))
            foreach (var (letter, spelling) in GermanSpellings) variant = variant.Replace(letter, spelling, StringComparison.Ordinal);
        else
            foreach (var (letter, spelling) in GermanSpellings) variant = variant.Replace(spelling, letter, StringComparison.Ordinal);
        return variant == word ? null : variant;
    }

    /// <summary>
    /// The query for passages or titles that hold any of the words or names. A name is searched as it is written,
    /// several words as one phrase. Null when there is nothing to search for.
    /// </summary>
    public static string? Expression(IEnumerable<string> words, IEnumerable<string>? names = null)
    {
        var terms = new List<string>();
        foreach (var word in words.SelectMany(Words).Take(MaximumWords))
            foreach (var form in Forms(word))
                terms.Add('"' + form + '"' + (new StringInfo(form).LengthInTextElements >= PrefixLength ? "*" : ""));
        var phrases = (names ?? []).Take(MaximumWords).Select(name => string.Join(' ', Words(name))).Where(phrase => phrase.Length > 0);
        terms.AddRange(phrases.SelectMany(Forms).Select(form => '"' + form + '"'));
        var distinct = terms.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 0 ? null : string.Join(" OR ", distinct);
    }

    /// <summary>A word as the index stores it: lowercased, without accents.</summary>
    public static string Folded(string word)
    {
        string decomposed;
        try { decomposed = word.Normalize(NormalizationForm.FormD); }
        // A lone surrogate cannot be normalized and has no accents to remove.
        catch (ArgumentException) { return word; }
        return Composed(string.Concat(decomposed.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)));
    }

    private static IEnumerable<string> Forms(string word) => Variant(word) is { } variant ? [word, variant] : [word];
}
