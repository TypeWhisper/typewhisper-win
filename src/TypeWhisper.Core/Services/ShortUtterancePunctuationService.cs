using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

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

        if (!IsShortUtterance(text))
            return text;

        return NormalizeShortUtterance(text);
    }

    /// <summary>
    /// Normalizes short-utterance punctuation in transcription text and segments.
    /// </summary>
    public static TranscriptionResult NormalizeResult(
        TranscriptionResult result,
        bool punctuationEnabled)
    {
        if (punctuationEnabled || !IsShortUtterance(result.Text))
            return result;

        return result with
        {
            Text = NormalizeShortUtterance(result.Text),
            Segments = result.Segments
                .Select(segment => segment with
                {
                    Text = NormalizeText(segment.Text, punctuationEnabled: false)
                })
                .ToList()
        };
    }

    private static bool IsShortUtterance(string text)
    {
        var wordCount = WordPattern().Count(text);
        return wordCount is >= 1 and <= MaximumWordCount;
    }

    private static string NormalizeShortUtterance(string text)
    {
        var normalized = GreetingCommaPattern().Replace(text, "${prefix}${greeting} ");
        normalized = TrailingGreetingCommaPattern().Replace(normalized, "${prefix}${greeting}");
        return TrailingSentencePunctuationPattern().Replace(normalized, string.Empty);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(
        @"^(?<prefix>\s*)(?<greeting>Hallo|Hello|Hi|Hey|Moin|Servus|Bonjour|Salut|Hola|Ciao|Olá|Ola|Hoi|Cześć|Ahoj|Hej|Hei|Moi|Привет|Здравствуйте|你好)\s*[,，、]\s*(?=[\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GreetingCommaPattern();

    [GeneratedRegex(
        @"^(?<prefix>\s*)(?<greeting>Hallo|Hello|Hi|Hey|Moin|Servus|Bonjour|Salut|Hola|Ciao|Olá|Ola|Hoi|Cześć|Ahoj|Hej|Hei|Moi|Привет|Здравствуйте|你好)\s*[,，、]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingGreetingCommaPattern();

    [GeneratedRegex(@"\s*[.!?…。！？]+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSentencePunctuationPattern();
}
