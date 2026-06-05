using System.Globalization;
using System.Text;

namespace TypeWhisper.Core.Services.NumberNormalization;

/// <summary>
/// Normalizes supported spoken number words into digits.
/// </summary>
public static class NumberWordNormalizer
{
    private static readonly HashSet<string> SupportedLanguageCodes = ["en", "de", "fr", "es", "zh", "ja"];
    private static readonly HashSet<char> CjkNumberCharacters = ['零', '〇', '一', '二', '两', '兩', '三', '四', '五', '六', '七', '八', '九', '十', '百', '千', '万', '萬', '亿', '億', '点', '點', '負', '负'];

    /// <summary>
    /// Normalizes supported number words in the supplied text for the requested language.
    /// </summary>
    public static string Normalize(string text, string? language)
    {
        var languageCode = NormalizeLanguageCode(language);
        if (languageCode is null || !SupportedLanguageCodes.Contains(languageCode) || string.IsNullOrEmpty(text))
            return text;

        var tokens = Tokenize(text);
        if (!tokens.Any(static token => token.IsWord))
            return text;

        var result = new StringBuilder();
        var index = 0;
        while (index < tokens.Count)
        {
            if (tokens[index].IsWord && ParseNumber(index, tokens, languageCode) is { } parsed)
            {
                result.Append(parsed.Replacement);
                index = parsed.EndIndex;
            }
            else
            {
                result.Append(tokens[index].Text);
                index++;
            }
        }

        return result.ToString();
    }

    internal static string? NormalizeLanguageCode(string? language)
    {
        var trimmed = language?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        var separatorIndex = trimmed.IndexOfAny(['-', '_']);
        var primary = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
        var normalized = primary.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    internal sealed record ParsedWords(string Value, int ConsumedWords);

    internal static string NormalizeWord(string word)
    {
        var normalized = word.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static ParsedNumber? ParseNumber(int index, IReadOnlyList<Token> tokens, string languageCode)
    {
        var words = WordCandidates(index, tokens);
        if (words.Count == 0)
            return null;

        var wordTexts = words.Select(static word => word.Text).ToArray();
        var parsed = languageCode switch
        {
            "en" => EnglishNumberWordParser.Parse(wordTexts),
            "de" => GermanNumberWordParser.Parse(wordTexts),
            "fr" => FrenchNumberWordParser.Parse(wordTexts),
            "es" => SpanishNumberWordParser.Parse(wordTexts),
            "zh" => ChineseNumberWordParser.Parse(wordTexts),
            "ja" => JapaneseNumberWordParser.Parse(wordTexts),
            _ => null
        };

        if (parsed is null || parsed.ConsumedWords <= 0 || parsed.ConsumedWords > words.Count)
            return null;

        var finalTokenIndex = words[parsed.ConsumedWords - 1].TokenIndex;
        return new ParsedNumber(parsed.Value, finalTokenIndex + 1);
    }

    private static List<WordCandidate> WordCandidates(int index, IReadOnlyList<Token> tokens)
    {
        var words = new List<WordCandidate>();
        var current = index;

        while (current < tokens.Count && tokens[current].IsWord)
        {
            words.Add(new WordCandidate(current, tokens[current].Text));

            var separatorIndex = current + 1;
            var nextWordIndex = current + 2;
            if (separatorIndex >= tokens.Count ||
                nextWordIndex >= tokens.Count ||
                !tokens[nextWordIndex].IsWord ||
                !IsWordConnector(tokens[separatorIndex].Text))
            {
                break;
            }

            current = nextWordIndex;
        }

        return words;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var current = new StringBuilder();
        TokenKind? currentKind = null;

        foreach (var character in text)
        {
            var kind = TokenKindFor(character);
            if (currentKind == kind)
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0 && currentKind is { } previousKind)
                tokens.Add(new Token(current.ToString(), previousKind));

            current.Clear();
            current.Append(character);
            currentKind = kind;
        }

        if (current.Length > 0 && currentKind is { } finalKind)
            tokens.Add(new Token(current.ToString(), finalKind));

        return tokens;
    }

    private static TokenKind TokenKindFor(char character)
    {
        if (CjkNumberCharacters.Contains(character))
            return TokenKind.CjkNumber;

        return char.IsLetter(character) ? TokenKind.Word : TokenKind.Other;
    }

    private static bool IsWordConnector(string text) =>
        text.Length > 0 && text.All(static c => char.IsWhiteSpace(c) || c == '-' || c == '\u2011');

    private enum TokenKind
    {
        Word,
        CjkNumber,
        Other
    }

    private sealed record Token(string Text, TokenKind Kind)
    {
        public bool IsWord => Kind is TokenKind.Word or TokenKind.CjkNumber;
    }

    private sealed record ParsedNumber(string Replacement, int EndIndex);
    private sealed record WordCandidate(int TokenIndex, string Text);
}
