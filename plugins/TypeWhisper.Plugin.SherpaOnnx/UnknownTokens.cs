using System.Text.RegularExpressions;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.SherpaOnnx;

/// <summary>
/// Parakeet's vocabulary has no token for a few characters: "&amp;", brackets, dashes, typographic quotes and apostrophes,
/// and the Greek final sigma "ς". Parakeet v3 leaves them out, but Ultra and Fluid Speech Pro learned to write them and
/// emit "&lt;unk&gt;" in their place. The context tells which character it stands for most of the time:
/// <list type="bullet">
/// <item>between capitals "&amp;" (M&amp;A, P&amp;L, R&amp;D), as in the macOS Parakeet plugin;</item>
/// <item>at the end of a lowercase Greek word "ς" ("τη&lt;unk&gt; εποχή&lt;unk&gt;" is "της εποχής");</item>
/// <item>before a capital an opening quote, so a space keeps the words apart ("film&lt;unk&gt;Il mistero" from «Il mistero»);</item>
/// <item>between lowercase letters an apostrophe (Maltese "f&lt;unk&gt;żewġ" is "f'żewġ").</item>
/// </list>
/// Anywhere else it is dropped, so the marker never reaches the user's text.
/// </summary>
internal static partial class UnknownTokens
{
    private const string Marker = "<unk>";

    /// <summary>Replaces or removes every marker and repairs the spacing it leaves behind.</summary>
    public static string Remove(string text)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal)) return text;
        var cleaned = BetweenCapitals().Replace(text, "&");
        cleaned = AfterGreekWord().Replace(cleaned, "ς");
        cleaned = BeforeCapital().Replace(cleaned, " ");
        cleaned = BetweenLowercase().Replace(cleaned, "'");
        cleaned = InsideWord().Replace(cleaned, string.Empty);
        cleaned = cleaned.Replace(Marker, " ", StringComparison.Ordinal);
        cleaned = RepeatedSpaces().Replace(cleaned, " ");
        cleaned = SpaceBeforeClosingPunctuation().Replace(cleaned, "$1");
        return cleaned.Trim();
    }

    /// <summary>
    /// Drops the marker tokens. Dictionary boosting looks up every token in the cleaned text, so one that is no longer
    /// there would make it skip the whole dictation.
    /// </summary>
    public static IReadOnlyList<VocabularyTokenTiming> Remove(IReadOnlyList<VocabularyTokenTiming> timings) =>
        timings.Any(IsMarker) ? timings.Where(timing => !IsMarker(timing)).ToArray() : timings;

    private static bool IsMarker(VocabularyTokenTiming timing) =>
        timing.Text.Replace('▁', ' ').Trim() == Marker;

    [GeneratedRegex(@"(?<=\p{Lu})<unk>(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex BetweenCapitals();

    // Lowercase Greek and Greek Extended, then the end of the word.
    [GeneratedRegex(@"(?<=[\u03AC-\u03CE\u1F00-\u1FFF])<unk>(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex AfterGreekWord();

    [GeneratedRegex(@"(?<=[\p{L}\p{N}])<unk>(?=\p{Lu})", RegexOptions.CultureInvariant)]
    private static partial Regex BeforeCapital();

    [GeneratedRegex(@"(?<=\p{Ll})<unk>(?=\p{Ll})", RegexOptions.CultureInvariant)]
    private static partial Regex BetweenLowercase();

    [GeneratedRegex(@"(?<=\w)<unk>(?=\w)", RegexOptions.CultureInvariant)]
    private static partial Regex InsideWord();

    [GeneratedRegex(" {2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedSpaces();

    [GeneratedRegex(@" +([,.;:!?)\]}…])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforeClosingPunctuation();
}
