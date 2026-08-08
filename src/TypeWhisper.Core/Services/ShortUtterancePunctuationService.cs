using System.Text.RegularExpressions;

namespace TypeWhisper.Core.Services;

/// <summary>
/// Normalizes model-supplied punctuation for short utterances when requested.
/// </summary>
public static partial class ShortUtterancePunctuationService
{
    private const int MaximumWordCount = 2;

    /// <summary>
    /// Removes greeting commas and sentence-ending punctuation from one- or two-word utterances.
    /// </summary>
    public static string NormalizeText(string text, bool punctuationEnabled)
    {
        if (punctuationEnabled || string.IsNullOrWhiteSpace(text))
            return text;

        var wordCount = WordPattern().Count(text);
        if (wordCount is < 1 or > MaximumWordCount)
            return text;

        var normalized = GreetingCommaPattern().Replace(text, " ");
        return TrailingSentencePunctuationPattern().Replace(normalized, string.Empty);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<=[\p{L}])\s*[,，]\s*(?=[\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex GreetingCommaPattern();

    [GeneratedRegex(@"\s*[.!?…。！？]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSentencePunctuationPattern();
}
