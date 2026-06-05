namespace TypeWhisper.Core.Services.NumberNormalization;

internal static class FrenchNumberWordParser
{
    private static readonly Dictionary<string, int> UnitValues = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["un"] = 1, ["une"] = 1, ["deux"] = 2, ["trois"] = 3, ["quatre"] = 4,
        ["cinq"] = 5, ["six"] = 6, ["sept"] = 7, ["huit"] = 8, ["neuf"] = 9
    };

    private static readonly Dictionary<string, int> TeenValues = new(StringComparer.Ordinal)
    {
        ["dix"] = 10, ["onze"] = 11, ["douze"] = 12, ["treize"] = 13, ["quatorze"] = 14,
        ["quinze"] = 15, ["seize"] = 16
    };

    private static readonly Dictionary<string, int> TensValues = new(StringComparer.Ordinal)
    {
        ["vingt"] = 20, ["trente"] = 30, ["quarante"] = 40,
        ["cinquante"] = 50, ["soixante"] = 60
    };

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var normalizedWords = words.Select(NumberWordNormalizer.NormalizeWord).ToArray();
        var index = 0;
        var isNegative = false;

        if (normalizedWords[index] == "moins")
        {
            isNegative = true;
            index++;
            if (index >= normalizedWords.Length)
                return null;
        }

        var integer = ParseInteger(normalizedWords, index, isNegative);
        if (integer is null)
            return null;

        index = integer.Value.NextIndex;
        var replacement = integer.Value.Value.ToString();

        if (index < normalizedWords.Length && normalizedWords[index] == "virgule")
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

    private static (int Value, int NextIndex)? ParseInteger(
        IReadOnlyList<string> words,
        int startIndex,
        bool allowLeadingArticleOne)
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

            if (word is "million" or "millions")
            {
                total += Math.Max(current, 1) * 1_000_000;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word == "mille")
            {
                total += Math.Max(current, 1) * 1_000;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word is "cent" or "cents")
            {
                current = Math.Max(current, 1) * 100;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            var allowArticleOne = AllowsArticleOne(index, words, startIndex, allowLeadingArticleOne, current, total);
            var segment = ParseUnderHundred(words, index, allowArticleOne);
            if (segment is null)
                break;

            if (lastWasPlainSmallNumber && segment.Value.Value < 10)
                break;

            current += segment.Value.Value;
            index = segment.Value.NextIndex;
            consumed = true;
            lastWasPlainSmallNumber = segment.Value.Value < 10 && !allowArticleOne;
        }

        return consumed ? (total + current, index) : null;
    }

    private static (int Value, int NextIndex)? ParseUnderHundred(
        IReadOnlyList<string> words,
        int startIndex,
        bool allowArticleOne)
    {
        if (startIndex >= words.Count)
            return null;

        var word = words[startIndex];
        if (word == "quatre" &&
            startIndex + 1 < words.Count &&
            words[startIndex + 1] is "vingt" or "vingts")
        {
            return AppendFrenchRemainder(80, words, startIndex + 2);
        }

        if (word == "dix" &&
            startIndex + 1 < words.Count &&
            UnitValue(words[startIndex + 1], true) is { } unit &&
            unit >= 7)
        {
            return (10 + unit, startIndex + 2);
        }

        if (TensValues.TryGetValue(word, out var ten))
            return AppendFrenchRemainder(ten, words, startIndex + 1);

        if (TeenValues.TryGetValue(word, out var teen))
            return (teen, startIndex + 1);

        if (UnitValue(word, allowArticleOne) is { } unitValue)
            return (unitValue, startIndex + 1);

        return null;
    }

    private static (int Value, int NextIndex) AppendFrenchRemainder(
        int baseValue,
        IReadOnlyList<string> words,
        int startIndex)
    {
        var index = startIndex;

        if (index < words.Count && words[index] == "et")
        {
            var afterEt = index + 1;
            if (afterEt < words.Count)
            {
                if (UnitValue(words[afterEt], true) is { } unit && unit == 1)
                    return (baseValue + unit, afterEt + 1);

                if (baseValue == 60 &&
                    TeenValues.TryGetValue(words[afterEt], out var teen) &&
                    teen == 11)
                {
                    return (baseValue + teen, afterEt + 1);
                }
            }

            return (baseValue, startIndex);
        }

        if (baseValue == 60 &&
            index < words.Count &&
            TeenValues.TryGetValue(words[index], out var sixtyTeen))
        {
            return (baseValue + sixtyTeen, index + 1);
        }

        if (baseValue == 80 && index < words.Count)
        {
            if (words[index] == "dix" &&
                index + 1 < words.Count &&
                UnitValue(words[index + 1], true) is { } unit &&
                unit >= 7)
            {
                return (90 + unit, index + 2);
            }

            if (TeenValues.TryGetValue(words[index], out var teen))
                return (baseValue + teen, index + 1);
        }

        if (index < words.Count &&
            UnitValue(words[index], true) is { } remainderUnit &&
            remainderUnit > 0)
        {
            return (baseValue + remainderUnit, index + 1);
        }

        return (baseValue, startIndex);
    }

    private static (string Digits, int NextIndex) ParseDecimalDigits(IReadOnlyList<string> words, int startIndex)
    {
        var digits = "";
        var index = startIndex;

        while (index < words.Count && UnitValue(words[index], true) is { } digit)
        {
            digits += digit.ToString();
            index++;
        }

        return (digits, index);
    }

    private static int? UnitValue(string word, bool allowArticleOne)
    {
        if (!UnitValues.TryGetValue(word, out var value))
            return null;

        if (value == 1 && !allowArticleOne)
            return null;

        return value;
    }

    private static bool AllowsArticleOne(
        int index,
        IReadOnlyList<string> words,
        int startIndex,
        bool allowLeadingArticleOne,
        int current,
        int total)
    {
        if (index == startIndex && allowLeadingArticleOne)
            return true;

        if (current >= 100 || total > 0)
            return true;

        return index + 1 < words.Count &&
               words[index + 1] is "cent" or "cents" or "mille" or "million" or "millions";
    }
}
