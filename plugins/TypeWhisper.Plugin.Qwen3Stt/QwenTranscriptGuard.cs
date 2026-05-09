using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Qwen3Stt;

public static partial class QwenTranscriptGuard
{
    public static string RemovingLikelyTrailingArtifact(string text, string? languageName)
    {
        if (!IsFrench(languageName))
            return text;

        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return text;

        var match = TrailingFrenchOuiRegex().Match(trimmed);
        if (!match.Success)
            return text;

        var prefix = trimmed[..match.Index].TrimEnd();
        if (prefix.Length == 0)
            return text;

        var punctuation = match.Value.TrimStart()[0];
        return $"{prefix}{punctuation}";
    }

    public static bool IsLikelyLooped(string text)
    {
        var words = Words(text);
        if (words.Count < 16)
            return false;

        var metrics = new LoopMetrics(words);
        var dominantShare = (double)metrics.MaxFrequency / words.Count;

        if (metrics.LongestRun >= 7)
            return true;
        if (dominantShare >= 0.5 && metrics.UniqueRatio <= 0.3)
            return true;
        if (metrics.HasRepeatedNGram(3, 5) && metrics.UniqueRatio <= 0.45)
            return true;
        return false;
    }

    public static string PreferredTranscript(string primary, string fallback)
    {
        var primaryMetrics = new LoopMetrics(Words(primary));
        var fallbackMetrics = new LoopMetrics(Words(fallback));

        if (primaryMetrics.QualityScore == fallbackMetrics.QualityScore)
            return primary.Length <= fallback.Length ? primary : fallback;
        return primaryMetrics.QualityScore >= fallbackMetrics.QualityScore ? primary : fallback;
    }

    private static bool IsFrench(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return false;

        return languageName
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name, "French", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Words(string text) =>
        WordRegex()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .ToList();

    [GeneratedRegex(@"[a-z0-9']+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(?i)[.!?;:]\s+oui[.!?]*$")]
    private static partial Regex TrailingFrenchOuiRegex();

    private readonly struct LoopMetrics
    {
        private readonly IReadOnlyList<string> _words;

        public LoopMetrics(IReadOnlyList<string> words)
        {
            _words = words;
            if (words.Count == 0)
            {
                UniqueRatio = 1;
                MaxFrequency = 0;
                LongestRun = 0;
                QualityScore = 0;
                return;
            }

            var counts = words.GroupBy(w => w).Select(g => g.Count()).ToArray();
            UniqueRatio = (double)counts.Length / words.Count;
            MaxFrequency = counts.Max();
            LongestRun = CalculateLongestRun(words);
            QualityScore = UniqueRatio - ((double)LongestRun / words.Count) - ((double)MaxFrequency / words.Count);
        }

        public double UniqueRatio { get; }
        public int MaxFrequency { get; }
        public int LongestRun { get; }
        public double QualityScore { get; }

        public bool HasRepeatedNGram(int n, int minRepeats)
        {
            if (_words.Count < n * minRepeats)
                return false;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i <= _words.Count - n; i++)
            {
                var key = string.Join('\u001f', _words.Skip(i).Take(n));
                counts.TryGetValue(key, out var count);
                counts[key] = count + 1;
            }

            return counts.Values.Any(count => count >= minRepeats);
        }

        private static int CalculateLongestRun(IReadOnlyList<string> words)
        {
            var longest = 1;
            var current = 1;
            for (var i = 1; i < words.Count; i++)
            {
                if (words[i] == words[i - 1])
                    current++;
                else
                    current = 1;
                longest = Math.Max(longest, current);
            }
            return longest;
        }
    }
}
