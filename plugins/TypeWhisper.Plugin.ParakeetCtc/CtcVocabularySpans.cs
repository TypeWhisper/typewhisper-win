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
    /// A transcript split into its words and one- to three-word windows, with each window's letters already lowercased.
    /// One rescoring compares every term with the same windows, so it prepares the transcript once.
    /// </summary>
    public sealed class Transcript
    {
        internal Transcript(string text)
        {
            Text = text;
            var words = Word.Matches(text);
            WordStarts = new int[words.Count];
            WordEnds = new int[words.Count];
            for (var i = 0; i < words.Count; i++) (WordStarts[i], WordEnds[i]) = (words[i].Index, words[i].Index + words[i].Length);
            var windows = new List<Window>(words.Count * MaximumWords);
            for (var first = 0; first < words.Count; first++)
            for (var count = 1; count <= MaximumWords && first + count <= words.Count; count++)
            {
                var (start, end) = (WordStarts[first], WordEnds[first + count - 1]);
                var original = text[start..end];
                windows.Add(new(first, count, start, end, original, Letters(original)));
            }
            Windows = [.. windows];
        }

        internal string Text { get; }
        internal int[] WordStarts { get; }
        internal int[] WordEnds { get; }
        internal Window[] Windows { get; }
    }

    internal readonly record struct Window(int First, int Count, int Start, int End, string Original, string Letters);

    /// <summary>Prepares <paramref name="text"/> for <see cref="Find(Transcript, string, float, out int, out int, out double)"/>.</summary>
    public static Transcript Prepare(string text) => new(text);

    /// <summary>
    /// Windows of <paramref name="text"/> that resemble <paramref name="term"/> at least as much as
    /// <paramref name="threshold"/>. A window is skipped when it is already the term, when it overlaps a place where
    /// the term is already written exactly, or when dropping its first or last word leaves a window that resembles the
    /// term at least as much.
    /// </summary>
    public static IReadOnlyList<Span> Find(string text, string term, float threshold, out int similarityRejected, out int spanRejected, out double bestSimilarity) =>
        Find(Prepare(text), term, threshold, out similarityRejected, out spanRejected, out bestSimilarity);

    /// <inheritdoc cref="Find(string, string, float, out int, out int, out double)"/>
    public static IReadOnlyList<Span> Find(Transcript transcript, string term, float threshold, out int similarityRejected, out int spanRejected, out double bestSimilarity)
    {
        similarityRejected = 0; spanRejected = 0; bestSimilarity = 0;
        var text = transcript.Text;
        var termLetters = Letters(term);
        var row = new int[termLetters.Length + 1];
        List<(int Start, int End)>? exact = null;
        foreach (var window in transcript.Windows)
            if (window.Original == term) (exact ??= []).Add((window.Start, window.End));

        var spans = new List<Span>();
        foreach (var (first, count, start, end, original, letters) in transcript.Windows)
        {
            // The edit distance is at least the length difference. A window that cannot reach the threshold that way, and
            // could not raise the best similarity either, is rejected without computing the distance.
            if (letters.Length > 0 && termLetters.Length > 0 && letters.Length <= 160
                && 1 - Math.Abs(letters.Length - termLetters.Length) / (double)Math.Max(letters.Length, termLetters.Length) is var bound
                && bound < threshold && bound <= bestSimilarity)
            { similarityRejected++; continue; }
            var similarity = Similarity(letters, termLetters, row);
            bestSimilarity = Math.Max(bestSimilarity, similarity);
            if (original == term || similarity < threshold) { similarityRejected++; continue; }
            if (exact is not null && exact.Any(e => e.Start < end && start < e.End)) { spanRejected++; continue; }
            if (count > 1)
            {
                var withoutFirst = text[transcript.WordStarts[first + 1]..end];
                var withoutLast = text[start..transcript.WordEnds[first + count - 2]];
                if (Similarity(Letters(withoutFirst), termLetters, row) >= similarity || Similarity(Letters(withoutLast), termLetters, row) >= similarity)
                { spanRejected++; continue; }
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
    public static double Similarity(string a, string b) => Similarity(Letters(a), Letters(b), null);

    private static string Letters(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    // Edit similarity of two Letters() strings. row is reused across calls; it needs room for b.Length + 1 values.
    private static double Similarity(string a, string b, int[]? row)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length > 160) return 0;
        if (row is null || row.Length < b.Length + 1) row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            { var previous = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1)); diagonal = previous; }
        }
        return 1 - row[b.Length] / (double)Math.Max(a.Length, b.Length);
    }
}
