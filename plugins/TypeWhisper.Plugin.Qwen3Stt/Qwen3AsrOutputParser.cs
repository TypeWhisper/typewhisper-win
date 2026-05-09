using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.Qwen3Stt;

public static partial class Qwen3AsrOutputParser
{
    public static Qwen3ParsedOutput Parse(string? raw, string? userLanguage)
    {
        var text = Normalize(raw);
        var forcedLanguageName = NormalizeLanguageName(userLanguage);
        if (string.IsNullOrWhiteSpace(text))
            return new Qwen3ParsedOutput(string.Empty, forcedLanguageName, Qwen3LanguageMapper.LanguageCodeForQwenLanguageName(forcedLanguageName));

        if (forcedLanguageName is not null)
            return new Qwen3ParsedOutput(text, forcedLanguageName, Qwen3LanguageMapper.LanguageCodeForQwenLanguageName(forcedLanguageName));

        var match = LanguageTaggedOutputRegex().Match(text);
        if (!match.Success)
            return new Qwen3ParsedOutput(text, null, null);

        var languageName = NormalizeLanguageName(match.Groups["language"].Value);
        var transcript = Normalize(match.Groups["text"].Value);
        var detectedCode = Qwen3LanguageMapper.LanguageCodeForQwenLanguageName(languageName);
        return new Qwen3ParsedOutput(transcript, languageName, detectedCode);
    }

    private static string? NormalizeLanguageName(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var trimmed = language.Trim();
        var qwenName = Qwen3LanguageMapper.ResolveLanguageName(trimmed);
        if (qwenName is not null)
            return qwenName;

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private static string Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"^language\s+(?<language>[^<\r\n]+)\s*<asr_text>\s*(?<text>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LanguageTaggedOutputRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record Qwen3ParsedOutput(string Text, string? LanguageName, string? DetectedLanguageCode);
