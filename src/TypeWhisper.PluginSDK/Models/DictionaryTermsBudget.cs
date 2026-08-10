namespace TypeWhisper.PluginSDK.Models;

/// <summary>
/// Describes the dictionary-term limits accepted by a transcription engine.
/// </summary>
/// <param name="MaxTerms">Maximum number of terms, or <see langword="null"/> when unlimited.</param>
/// <param name="MaxCharsPerTerm">Maximum number of characters per term, or <see langword="null"/> when unlimited.</param>
/// <param name="MaxWordsPerTerm">Maximum number of whitespace-separated words per term, or <see langword="null"/> when unlimited.</param>
/// <param name="MaxTotalChars">Maximum prompt length including comma separators, or <see langword="null"/> when unlimited.</param>
public sealed record DictionaryTermsBudget(
    int? MaxTerms = null,
    int? MaxCharsPerTerm = null,
    int? MaxWordsPerTerm = null,
    int? MaxTotalChars = null)
{
    /// <summary>
    /// Conservative fallback used by plugins that support dictionary terms without declaring provider limits.
    /// </summary>
    public static DictionaryTermsBudget Default { get; } = new(MaxTotalChars: 600);
}

/// <summary>
/// Normalizes and formats dictionary terms according to an engine's advertised budget.
/// </summary>
public static class PluginDictionaryTerms
{
    /// <summary>
    /// Returns normalized, de-duplicated terms in their original order.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? terms)
    {
        if (terms is null)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var rawTerm in terms)
        {
            var term = rawTerm?.Trim();
            if (string.IsNullOrEmpty(term) || !seen.Add(term))
                continue;

            normalized.Add(term);
        }

        return normalized;
    }

    /// <summary>
    /// Applies per-term filters before count and total-length limits.
    /// </summary>
    public static IReadOnlyList<string> Clip(
        IEnumerable<string>? terms,
        DictionaryTermsBudget? budget)
    {
        IEnumerable<string> clipped = Normalize(terms);
        if (budget?.MaxCharsPerTerm is { } maxCharsPerTerm)
        {
            var safeMaxCharsPerTerm = Math.Max(0, maxCharsPerTerm);
            clipped = clipped.Where(term => term.Length <= safeMaxCharsPerTerm);
        }

        if (budget?.MaxWordsPerTerm is { } maxWordsPerTerm)
        {
            var safeMaxWordsPerTerm = Math.Max(0, maxWordsPerTerm);
            clipped = clipped.Where(term =>
                term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= safeMaxWordsPerTerm);
        }

        var result = clipped.ToList();
        if (budget?.MaxTerms is { } maxTerms)
            result = result.Take(Math.Max(0, maxTerms)).ToList();

        if (budget?.MaxTotalChars is not { } maxTotalChars)
            return result;

        var safeMaxTotalChars = Math.Max(0, maxTotalChars);
        var limited = new List<string>();
        var totalChars = 0;
        foreach (var term in result)
        {
            var separatorChars = limited.Count == 0 ? 0 : 2;
            var nextTotal = totalChars + separatorChars + term.Length;
            if (nextTotal > safeMaxTotalChars)
                break;

            limited.Add(term);
            totalChars = nextTotal;
        }

        return limited;
    }

    /// <summary>
    /// Builds the comma-separated prompt accepted by transcription plugins.
    /// </summary>
    public static string? CreatePrompt(
        IEnumerable<string>? terms,
        DictionaryTermsBudget? budget = null)
    {
        var clipped = Clip(terms, budget ?? DictionaryTermsBudget.Default);
        return clipped.Count == 0 ? null : string.Join(", ", clipped);
    }
}
