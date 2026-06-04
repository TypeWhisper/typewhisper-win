using System.Diagnostics;
using System.Globalization;
using System.Text;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Provides vocabulary boosting service behavior.
/// </summary>
public sealed class VocabularyBoostingService : IVocabularyBoostingService
{
    private const int MaxWindowTokens = 4;
    private const int MaxReplacements = 10;
    private const double AmbiguityMargin = 0.08;

    private readonly IDictionaryService _dictionary;
    private readonly object _sync = new();
    private IReadOnlyList<NormalizedTerm> _terms = [];

    /// <summary>
    /// Initializes a new instance of the VocabularyBoostingService class.
    /// </summary>
    public VocabularyBoostingService(IDictionaryService dictionary)
    {
        _dictionary = dictionary;
        _dictionary.EntriesChanged += RebuildCatalog;
        RebuildCatalog();
    }

    /// <summary>
    /// Applies the configured transformation to the supplied input.
    /// </summary>
    public string Apply(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return rawText;

        IReadOnlyList<NormalizedTerm> terms;
        lock (_sync)
        {
            terms = _terms;
        }

        if (terms.Count == 0)
        {
            Debug.WriteLine("VocabularyBoosting: candidates=0 replacements=0");
            return rawText;
        }

        try
        {
            var tokens = Tokenize(rawText);
            if (tokens.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={terms.Count} replacements=0");
                return rawText;
            }

            var proposals = FindProposals(rawText, tokens, terms);
            if (proposals.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={terms.Count} replacements=0");
                return rawText;
            }

            proposals.Sort(static (left, right) =>
            {
                var byTokenCount = right.Term.TokenCount.CompareTo(left.Term.TokenCount);
                if (byTokenCount != 0) return byTokenCount;

                var byLength = right.Term.Normalized.Length.CompareTo(left.Term.Normalized.Length);
                if (byLength != 0) return byLength;

                var byManual = left.Term.IsPack.CompareTo(right.Term.IsPack);
                if (byManual != 0) return byManual;

                var byScore = right.Score.CompareTo(left.Score);
                if (byScore != 0) return byScore;

                return left.Start.CompareTo(right.Start);
            });

            var accepted = new List<Replacement>(Math.Min(MaxReplacements, proposals.Count));
            foreach (var proposal in proposals)
            {
                if (accepted.Count >= MaxReplacements)
                    break;

                if (accepted.Any(existing => Overlaps(existing, proposal)))
                    continue;

                accepted.Add(proposal);
            }

            if (accepted.Count == 0)
            {
                Debug.WriteLine($"VocabularyBoosting: candidates={terms.Count} replacements=0");
                return rawText;
            }

            var rewritten = ApplyReplacements(rawText, accepted);
            Debug.WriteLine($"VocabularyBoosting: candidates={terms.Count} replacements={accepted.Count}");
            return rewritten;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VocabularyBoosting failed: {ex.Message}");
            return rawText;
        }
    }

    private void RebuildCatalog()
    {
        try
        {
            var terms = _dictionary.Entries
                .Where(entry =>
                    entry.IsEnabled &&
                    entry.EntryType == DictionaryEntryType.Term &&
                    !string.IsNullOrWhiteSpace(entry.Original))
                .SelectMany(CreateNormalizedTerms)
                .GroupBy(term => term.Normalized, StringComparer.Ordinal)
                .Select(group => group
                    .OrderBy(term => term.IsPack)
                    .ThenByDescending(term => term.TokenCount)
                    .ThenByDescending(term => term.Normalized.Length)
                    .First())
                .OrderByDescending(term => term.TokenCount)
                .ThenByDescending(term => term.Normalized.Length)
                .ThenBy(term => term.IsPack)
                .ToArray();

            lock (_sync)
            {
                _terms = terms;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"VocabularyBoosting catalog rebuild failed: {ex.Message}");
            lock (_sync)
            {
                _terms = [];
            }
        }
    }

    private static List<Replacement> FindProposals(
        string rawText,
        IReadOnlyList<TokenSpan> tokens,
        IReadOnlyList<NormalizedTerm> terms)
    {
        var proposals = new List<Replacement>();

        for (var startIndex = 0; startIndex < tokens.Count; startIndex++)
        {
            var maxWindowLength = Math.Min(MaxWindowTokens, tokens.Count - startIndex);
            for (var windowLength = 1; windowLength <= maxWindowLength; windowLength++)
            {
                var endIndex = startIndex + windowLength - 1;
                var spanStart = tokens[startIndex].Start;
                var spanEnd = tokens[endIndex].End;
                var rawSpan = rawText[spanStart..spanEnd];
                var trimmed = TrimWindow(rawSpan);
                if (trimmed.CoreLength <= 0)
                    continue;

                var coreText = rawSpan.Substring(trimmed.CoreStartOffset, trimmed.CoreLength);
                var normalizedWindow = Normalize(coreText);
                if (string.IsNullOrEmpty(normalizedWindow))
                    continue;

                var scoredCandidates = new List<ScoredCandidate>();
                foreach (var term in terms)
                {
                    if (!IsCompatibleWindow(term, normalizedWindow, windowLength))
                        continue;

                    if (string.Equals(coreText, term.OutputText, StringComparison.Ordinal))
                        continue;

                    var score = Score(term, normalizedWindow, windowLength);
                    if (score is null)
                        continue;

                    scoredCandidates.Add(new ScoredCandidate(term, score.Value));
                }

                if (scoredCandidates.Count == 0)
                    continue;

                scoredCandidates.Sort(static (left, right) =>
                {
                    var byScore = right.Score.CompareTo(left.Score);
                    if (byScore != 0) return byScore;

                    var byTokenCount = right.Term.TokenCount.CompareTo(left.Term.TokenCount);
                    if (byTokenCount != 0) return byTokenCount;

                    var byLength = right.Term.Normalized.Length.CompareTo(left.Term.Normalized.Length);
                    if (byLength != 0) return byLength;

                    return left.Term.IsPack.CompareTo(right.Term.IsPack);
                });

                var best = scoredCandidates[0];
                var secondScore = scoredCandidates.Count > 1 ? scoredCandidates[1].Score : double.NegativeInfinity;
                if (scoredCandidates.Count > 1 && best.Score - secondScore < AmbiguityMargin)
                    continue;

                proposals.Add(new Replacement(
                    spanStart + trimmed.CoreStartOffset,
                    spanStart + trimmed.CoreStartOffset + trimmed.CoreLength,
                    best.Term.OutputText,
                    best.Score,
                    best.Term));
            }
        }

        return proposals;
    }

    private static bool IsCompatibleWindow(NormalizedTerm term, string normalizedWindow, int windowTokenCount)
    {
        if (term.TokenCount > MaxWindowTokens)
            return false;

        if (Math.Abs(term.TokenCount - windowTokenCount) > 1)
            return false;

        var lengthDifference = Math.Abs(term.Normalized.Length - normalizedWindow.Length);
        if (term.TokenCount == 1)
            return lengthDifference <= 2;

        var maxAllowedDifference = Math.Max(3, term.Normalized.Length / 3);
        return lengthDifference <= maxAllowedDifference;
    }

    private static double? Score(NormalizedTerm term, string normalizedWindow, int windowTokenCount)
    {
        var maxLength = Math.Max(term.Normalized.Length, normalizedWindow.Length);
        if (maxLength == 0)
            return null;

        var lengthDifference = Math.Abs(term.Normalized.Length - normalizedWindow.Length);
        var distance = LevenshteinDistance(term.Normalized, normalizedWindow);
        var charSimilarity = 1d - (double)distance / maxLength;
        var sameFirst = term.FirstAlphaNumeric == GetFirstAlphaNumeric(normalizedWindow);
        var sameLast = term.LastAlphaNumeric == GetLastAlphaNumeric(normalizedWindow);

        if (term.TokenCount == 1)
        {
            if (!sameFirst || !sameLast)
                return null;

            if (lengthDifference > 2 || charSimilarity < 0.86d)
                return null;
        }
        else
        {
            if (Math.Abs(term.TokenCount - windowTokenCount) > 1 || charSimilarity < 0.80d)
                return null;
        }

        var score = charSimilarity;
        if (sameFirst)
            score += 0.02d;
        if (sameLast)
            score += 0.02d;
        if (term.TokenCount == windowTokenCount)
            score += 0.03d;
        if (lengthDifference >= 3)
            score -= 0.03d;

        return score;
    }

    private static string ApplyReplacements(string rawText, IReadOnlyList<Replacement> replacements)
    {
        var ordered = replacements.OrderByDescending(replacement => replacement.Start);
        var builder = new StringBuilder(rawText);

        foreach (var replacement in ordered)
        {
            builder.Remove(replacement.Start, replacement.End - replacement.Start);
            builder.Insert(replacement.Start, replacement.ReplacementText);
        }

        return builder.ToString();
    }

    private static bool Overlaps(Replacement left, Replacement right) =>
        left.Start < right.End && right.Start < left.End;

    private static IEnumerable<NormalizedTerm> CreateNormalizedTerms(DictionaryEntry entry)
    {
        var outputText = string.IsNullOrWhiteSpace(entry.Replacement)
            ? entry.Original.Trim()
            : entry.Replacement.Trim();

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entry.Original.Trim()
        };

        if (!string.IsNullOrWhiteSpace(entry.Replacement))
            aliases.Add(entry.Replacement.Trim());

        var isPack = entry.Id.StartsWith("pack:", StringComparison.Ordinal);
        foreach (var alias in aliases)
        {
            var normalized = Normalize(alias);
            if (string.IsNullOrEmpty(normalized))
                continue;

            var tokenCount = CountTokens(normalized);
            if (tokenCount == 0)
                continue;

            yield return new NormalizedTerm(
                outputText,
                normalized,
                tokenCount,
                isPack,
                GetFirstAlphaNumeric(normalized),
                GetLastAlphaNumeric(normalized));
        }
    }

    private static List<TokenSpan> Tokenize(string text)
    {
        var tokens = new List<TokenSpan>();
        var index = 0;

        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            if (index >= text.Length)
                break;

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
                index++;

            tokens.Add(new TokenSpan(start, index));
        }

        return tokens;
    }

    private static WindowTrim TrimWindow(string rawSpan)
    {
        var start = 0;
        var end = rawSpan.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(rawSpan[start]))
            start++;

        while (end >= start && !char.IsLetterOrDigit(rawSpan[end]))
            end--;

        return end < start
            ? new WindowTrim(0, 0)
            : new WindowTrim(start, end - start + 1);
    }

    private static int CountTokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '/')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');

                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
                continue;
            }

            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(ch);
            }
        }

        var normalized = CollapseSpaces(builder.ToString());
        return TrimNonAlphaNumericEdges(normalized);
    }

    private static string CollapseSpaces(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    builder.Append(' ');

                lastWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string TrimNonAlphaNumericEdges(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var start = 0;
        var end = value.Length - 1;

        while (start <= end && !char.IsLetterOrDigit(value[start]))
            start++;

        while (end >= start && !char.IsLetterOrDigit(value[end]))
            end--;

        return end < start ? string.Empty : value[start..(end + 1)];
    }

    private static char? GetFirstAlphaNumeric(string text) =>
        text.FirstOrDefault(char.IsLetterOrDigit) is var ch && ch != default ? ch : null;

    private static char? GetLastAlphaNumeric(string text) =>
        text.LastOrDefault(char.IsLetterOrDigit) is var ch && ch != default ? ch : null;

    private static int LevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
            return target.Length;
        if (target.Length == 0)
            return source.Length;

        var previous = new int[target.Length + 1];
        var current = new int[target.Length + 1];

        for (var j = 0; j <= target.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= target.Length; j++)
            {
                var substitutionCost = source[i - 1] == target[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[target.Length];
    }

    private sealed record NormalizedTerm(
        string OutputText,
        string Normalized,
        int TokenCount,
        bool IsPack,
        char? FirstAlphaNumeric,
        char? LastAlphaNumeric);

    private sealed record ScoredCandidate(NormalizedTerm Term, double Score);

    private sealed record Replacement(
        int Start,
        int End,
        string ReplacementText,
        double Score,
        NormalizedTerm Term);

    private readonly record struct TokenSpan(int Start, int End);

    private readonly record struct WindowTrim(int CoreStartOffset, int CoreLength);
}
