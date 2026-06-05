namespace TypeWhisper.Core.Services.NumberNormalization;

internal static class GermanNumberWordParser
{
    private static readonly Dictionary<string, int> Units = new(StringComparer.Ordinal)
    {
        ["null"] = 0, ["eins"] = 1, ["ein"] = 1, ["eine"] = 1, ["einen"] = 1, ["einem"] = 1, ["einer"] = 1,
        ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["funf"] = 5, ["fuenf"] = 5,
        ["sechs"] = 6, ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9
    };

    private static readonly Dictionary<string, int> Teens = new(StringComparer.Ordinal)
    {
        ["zehn"] = 10, ["elf"] = 11, ["zwolf"] = 12, ["zwoelf"] = 12, ["dreizehn"] = 13, ["vierzehn"] = 14,
        ["funfzehn"] = 15, ["fuenfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17,
        ["achtzehn"] = 18, ["neunzehn"] = 19
    };

    private static readonly Dictionary<string, int> Tens = new(StringComparer.Ordinal)
    {
        ["zwanzig"] = 20, ["dreissig"] = 30, ["dreizig"] = 30, ["vierzig"] = 40,
        ["funfzig"] = 50, ["fuenfzig"] = 50, ["sechzig"] = 60, ["siebzig"] = 70,
        ["achtzig"] = 80, ["neunzig"] = 90
    };

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var normalizedWords = words.Select(NormalizeWord).ToArray();
        var index = 0;
        var isNegative = false;

        if (normalizedWords[index] == "minus")
        {
            isNegative = true;
            index++;
            if (index >= normalizedWords.Length)
                return null;
        }

        var integer = ParseInteger(normalizedWords, index);
        if (integer is null)
            return null;

        index = integer.Value.NextIndex;
        var replacement = integer.Value.Value.ToString();

        if (index < normalizedWords.Length && normalizedWords[index] == "komma")
        {
            var decimalPart = ParseDecimalDigits(normalizedWords, index + 1);
            if (decimalPart.Digits.Length > 0)
            {
                replacement += "," + decimalPart.Digits;
                index = decimalPart.NextIndex;
            }
        }

        if (isNegative)
            replacement = "-" + replacement;

        return new NumberWordNormalizer.ParsedWords(replacement, index);
    }

    private static (int Value, int NextIndex)? ParseInteger(IReadOnlyList<string> words, int startIndex)
    {
        if (startIndex >= words.Count)
            return null;

        var total = 0;
        var current = 0;
        var index = startIndex;
        var consumed = false;
        var lastWasPlainSmallNumber = false;

        while (index < words.Count)
        {
            var word = words[index];

            if (word == "und" &&
                current > 0 &&
                current < 10 &&
                index + 1 < words.Count &&
                Tens.TryGetValue(words[index + 1], out var tenValue))
            {
                current += tenValue;
                index += 2;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word == "hundert")
            {
                current = Math.Max(current, 1) * 100;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word is "tausend" or "million" or "millionen")
            {
                var scale = word == "tausend" ? 1_000 : 1_000_000;
                total += Math.Max(current, 1) * scale;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            var allowsArticleOne = AllowsArticleOne(index, words);
            var value = ParseCompound(word, allowsArticleOne);
            if (value is null)
                break;

            if (lastWasPlainSmallNumber && value.Value < 10)
                break;

            current += value.Value;
            index++;
            consumed = true;
            lastWasPlainSmallNumber = value.Value < 10 && !allowsArticleOne;
        }

        return consumed ? (total + current, index) : null;
    }

    private static (string Digits, int NextIndex) ParseDecimalDigits(IReadOnlyList<string> words, int startIndex)
    {
        var digits = "";
        var index = startIndex;

        while (index < words.Count && DigitValue(words[index]) is { } digit)
        {
            digits += digit.ToString();
            index++;
        }

        return (digits, index);
    }

    private static int? ParseCompound(string word, bool allowArticleOne)
    {
        if (DirectValue(word, allowArticleOne) is { } direct)
            return direct;

        var thousandIndex = word.IndexOf("tausend", StringComparison.Ordinal);
        if (thousandIndex >= 0)
        {
            var prefix = word[..thousandIndex];
            var suffix = word[(thousandIndex + "tausend".Length)..];
            var prefixValue = prefix.Length == 0 ? 1 : ParseCompound(prefix, true);
            if (prefixValue is null)
                return null;
            var suffixValue = suffix.Length == 0 ? 0 : ParseCompound(suffix, true);
            return suffixValue is null ? null : prefixValue.Value * 1_000 + suffixValue.Value;
        }

        var hundredIndex = word.IndexOf("hundert", StringComparison.Ordinal);
        if (hundredIndex >= 0)
        {
            var prefix = word[..hundredIndex];
            var suffix = word[(hundredIndex + "hundert".Length)..];
            var prefixValue = prefix.Length == 0 ? 1 : ParseUnderHundred(prefix, true);
            if (prefixValue is null)
                return null;
            var suffixValue = suffix.Length == 0 ? 0 : ParseUnderHundred(suffix, true);
            return suffixValue is null ? null : prefixValue.Value * 100 + suffixValue.Value;
        }

        return ParseUnderHundred(word, allowArticleOne);
    }

    private static int? ParseUnderHundred(string word, bool allowArticleOne)
    {
        if (DirectValue(word, allowArticleOne) is { } direct)
            return direct;

        var undIndex = word.IndexOf("und", StringComparison.Ordinal);
        if (undIndex >= 0)
        {
            var prefix = word[..undIndex];
            var suffix = word[(undIndex + "und".Length)..];
            if (DirectUnitValue(prefix, true) is { } unit &&
                unit > 0 &&
                unit < 10 &&
                Tens.TryGetValue(suffix, out var tenValue))
            {
                return unit + tenValue;
            }
        }

        return null;
    }

    private static int? DirectValue(string word, bool allowArticleOne) =>
        DirectUnitValue(word, allowArticleOne) is { } unit
            ? unit
            : Teens.TryGetValue(word, out var teen)
                ? teen
                : Tens.TryGetValue(word, out var ten)
                    ? ten
                    : null;

    private static int? DirectUnitValue(string word, bool allowArticleOne)
    {
        if (!Units.TryGetValue(word, out var value))
            return null;

        if (value == 1 && word != "eins" && !allowArticleOne)
            return null;

        return value;
    }

    private static int? DigitValue(string word) => DirectUnitValue(word, false);

    private static bool AllowsArticleOne(int index, IReadOnlyList<string> words) =>
        index + 1 < words.Count && words[index + 1] is "hundert" or "tausend" or "million" or "millionen";

    private static string NormalizeWord(string word) =>
        NumberWordNormalizer.NormalizeWord(word).Replace("ß", "ss", StringComparison.Ordinal);
}
