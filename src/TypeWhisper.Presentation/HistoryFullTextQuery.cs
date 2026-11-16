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

    /// <summary>The words of a text as the index splits them, lowercased.</summary>
    public static IReadOnlyList<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) word.Append(Rune.ToLowerInvariant(rune).ToString());
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

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
        foreach (var name in (names ?? []).Take(MaximumWords))
        {
            var phrase = string.Join(' ', Words(name));
            if (phrase.Length == 0) continue;
            foreach (var form in Forms(phrase)) terms.Add('"' + form + '"');
        }
        var distinct = terms.Distinct(StringComparer.Ordinal).ToArray();
        return distinct.Length == 0 ? null : string.Join(" OR ", distinct);
    }

    /// <summary>A word as the index stores it: lowercased, without accents.</summary>
    public static string Folded(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark) folded.Append(character);
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    private static IEnumerable<string> Forms(string word) => Variant(word) is { } variant ? [word, variant] : [word];
}
