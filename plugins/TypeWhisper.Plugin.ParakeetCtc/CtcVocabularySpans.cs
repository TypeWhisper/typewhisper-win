using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.ParakeetCtc;

/// <summary>
/// Chooses which transcript words a vocabulary term may replace. A term replaces a window of one to three words,
/// so the window must not reach beyond the words that resemble the term: an extra word would be replaced too.
/// </summary>
public static class CtcVocabularySpans
{
    public const int MaximumWords = 3;
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    /// <summary>A replaceable window in UTF-16 coordinates of the transcript.</summary>
    public readonly record struct Span(int Start, int Length, double Similarity);

    /// <summary>
    /// Windows of <paramref name="text"/> that resemble <paramref name="term"/> at least as much as
    /// <paramref name="threshold"/>. A window is skipped when it is already the term, when it overlaps a place where
    /// the term is already written exactly, or when dropping its first or last word leaves a window that resembles the
    /// term at least as much.
    /// </summary>
    public static IReadOnlyList<Span> Find(string text, string term, float threshold, out int similarityRejected, out int spanRejected, out double bestSimilarity)
    {
        similarityRejected = 0; spanRejected = 0; bestSimilarity = 0;
        var words = Word.Matches(text).Cast<Match>().ToArray();
        var windows = new List<(int First, int Count, int Start, int End)>();
        for (var first = 0; first < words.Length; first++)
        for (var count = 1; count <= MaximumWords && first + count <= words.Length; count++)
            windows.Add((first, count, words[first].Index, words[first + count - 1].Index + words[first + count - 1].Length));
        var exact = windows.Where(w => text[w.Start..w.End] == term).ToArray();

        var spans = new List<Span>();
        foreach (var (first, count, start, end) in windows)
        {
            var original = text[start..end];
            var similarity = Similarity(original, term);
            bestSimilarity = Math.Max(bestSimilarity, similarity);
            if (original == term || similarity < threshold) { similarityRejected++; continue; }
            if (exact.Any(e => e.Start < end && start < e.End)) { spanRejected++; continue; }
            if (count > 1)
            {
                var withoutFirst = text[words[first + 1].Index..end];
                var withoutLast = text[start..(words[first + count - 2].Index + words[first + count - 2].Length)];
                if (Similarity(withoutFirst, term) >= similarity || Similarity(withoutLast, term) >= similarity) { spanRejected++; continue; }
            }
            spans.Add(new(start, end - start, similarity));
        }
        return spans;
    }

    /// <summary>
    /// Keeps non-overlapping replacements. Where acoustically accepted proposals overlap, the window that resembles
    /// its term most wins, then the higher acoustic score; the score alone favors windows with extra words, whose
    /// original text scores worse.
    /// </summary>
    public static IReadOnlyList<VocabularyReplacement> Select(IEnumerable<(VocabularyReplacement Replacement, double Similarity)> proposals)
    {
        var selected = new List<VocabularyReplacement>();
        foreach (var (proposal, _) in proposals.OrderByDescending(p => p.Similarity).ThenByDescending(p => p.Replacement.Score))
            if (!selected.Any(p => p.Start < proposal.Start + proposal.Length && proposal.Start < p.Start + p.Length)) selected.Add(proposal);
        return selected.OrderBy(p => p.Start).ToArray();
    }

    /// <summary>Normalized edit similarity of the letters and digits of both texts, ignoring case.</summary>
    public static double Similarity(string a, string b)
    {
        a = string.Concat(a.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        b = string.Concat(b.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0 || a.Length > 160) return 0;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            { var previous = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = previous; }
        }
        return 1 - row[^1] / (double)Math.Max(a.Length, b.Length);
    }
}
