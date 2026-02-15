using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services;

public static class TextDiffService
{
    public static bool HasChanges(string rawText, string finalText)
        => !string.Equals(rawText, finalText, StringComparison.Ordinal);

    public static List<CorrectionSuggestion> ExtractCorrections(string original, string edited)
    {
        if (!HasChanges(original, edited))
            return [];

        var origWords = original.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var editWords = edited.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // If more than 50% changed, it's a rewrite — no suggestions
        var lcsLen = LcsLength(origWords, editWords);
        var maxLen = Math.Max(origWords.Length, editWords.Length);
        if (maxLen > 0 && (double)lcsLen / maxLen < 0.5)
            return [];

        // Build edit script from LCS
        var lcs = LcsTable(origWords, editWords);
        var removals = new List<(int Position, string Word)>();
        var insertions = new List<(int Position, string Word)>();

        int i = origWords.Length, j = editWords.Length;
        while (i > 0 || j > 0)
        {
            if (i > 0 && j > 0 && string.Equals(origWords[i - 1], editWords[j - 1], StringComparison.Ordinal))
            {
                i--;
                j--;
            }
            else if (j > 0 && (i == 0 || lcs[i, j - 1] >= lcs[i - 1, j]))
            {
                insertions.Add((j - 1, editWords[j - 1]));
                j--;
            }
            else
            {
                removals.Add((i - 1, origWords[i - 1]));
                i--;
            }
        }

        // Pair removals with nearby insertions (±3 positions)
        var suggestions = new List<CorrectionSuggestion>();
        var usedInsertions = new HashSet<int>();

        foreach (var (rPos, rWord) in removals)
        {
            int bestIdx = -1;
            int bestDist = int.MaxValue;
            for (var k = 0; k < insertions.Count; k++)
            {
                if (usedInsertions.Contains(k)) continue;
                var dist = Math.Abs(insertions[k].Position - rPos);
                if (dist <= 3 && dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = k;
                }
            }

            if (bestIdx >= 0)
            {
                var iWord = insertions[bestIdx].Word;
                usedInsertions.Add(bestIdx);

                // Skip punctuation-only changes
                if (IsPunctuationOnly(rWord) && IsPunctuationOnly(iWord))
                    continue;

                // Skip if only case/punctuation differ at boundaries
                var rClean = rWord.Trim('.', ',', '!', '?', ':', ';');
                var iClean = iWord.Trim('.', ',', '!', '?', ':', ';');
                if (string.Equals(rClean, iClean, StringComparison.OrdinalIgnoreCase) && rClean == iClean)
                    continue;

                suggestions.Add(new CorrectionSuggestion(rWord, iWord));
            }
        }

        return suggestions;
    }

    private static bool IsPunctuationOnly(string word) =>
        word.All(c => char.IsPunctuation(c) || char.IsSymbol(c));

    private static int LcsLength(string[] a, string[] b)
    {
        var table = LcsTable(a, b);
        return table[a.Length, b.Length];
    }

    private static int[,] LcsTable(string[] a, string[] b)
    {
        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (var i = 1; i <= m; i++)
        for (var j = 1; j <= n; j++)
        {
            dp[i, j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
                ? dp[i - 1, j - 1] + 1
                : Math.Max(dp[i - 1, j], dp[i, j - 1]);
        }

        return dp;
    }
}
