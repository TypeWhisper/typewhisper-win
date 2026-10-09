using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Collapses a word spoken three or more times in a row, such as a stuttered
/// "ich ich ich", to its first occurrence. Two repetitions stay because they are
/// often intentional ("very very good").
/// </summary>
public static class RepeatedWordCollapser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // Repetitions may be separated by spaces or tabs, optionally after a comma, because
    // speech models such as Parakeet write a stutter as "ich, ich, ich". Other punctuation
    // and line breaks between repetitions mark them as deliberate and keep them.
    private static readonly Regex Repetition = new(
        @"(?<![\p{L}\p{N}_'’])(?<word>\p{L}+)(?:,?[ \t]+\k<word>(?![\p{L}\p{N}_'’])){2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>Replaces each run of three or more identical words with its first word.</summary>
    public static string Collapse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        try
        {
            return Repetition.Replace(text, static match => match.Groups["word"].Value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological input must not block later post-processing.
            return text;
        }
    }
}
