using System.Text.Json;

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
    private const string StructuredPrefix = "TypeWhisper.DictionaryTerms/1\n";
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

    /// <summary>Encodes provider-budgeted terms without losing punctuation or term boundaries.</summary>
    public static string? CreateStructuredPrompt(IEnumerable<string>? terms, DictionaryTermsBudget? budget = null)
    {
        var clipped = Clip(terms, budget ?? DictionaryTermsBudget.Default);
        return clipped.Count == 0 ? null : StructuredPrefix + JsonSerializer.Serialize(clipped);
    }

    /// <summary>Reads the opt-in structured representation, or an older host's delimited prompt.
    /// Malformed structured input fails explicitly instead of becoming unintended vocabulary.</summary>
    public static IReadOnlyList<string> ParsePrompt(string? prompt, char[]? legacySeparators = null)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return [];
        if (prompt.StartsWith(StructuredPrefix, StringComparison.Ordinal))
            return Normalize(JsonSerializer.Deserialize<string[]>(prompt.AsSpan(StructuredPrefix.Length))
                ?? throw new JsonException("Dictionary terms must be an array."));
        return Normalize(prompt.Split(legacySeparators ?? [','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Unwraps structured terms for provider endpoints accepting only a plain text prompt.</summary>
    public static string? ToPlainPrompt(string? prompt) =>
        prompt is not null && prompt.StartsWith(StructuredPrefix, StringComparison.Ordinal)
            ? string.Join(", ", ParsePrompt(prompt)) : prompt;

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
