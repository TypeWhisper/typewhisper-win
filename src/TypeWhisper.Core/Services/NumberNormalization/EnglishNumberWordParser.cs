namespace TypeWhisper.Core.Services.NumberNormalization;

internal static class EnglishNumberWordParser
{
    private static readonly Dictionary<string, int> UnitValues = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9
    };

    private static readonly Dictionary<string, int> TeenValues = new(StringComparer.Ordinal)
    {
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14,
        ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19
    };

    private static readonly Dictionary<string, int> TensValues = new(StringComparer.Ordinal)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
    };

    private static readonly Dictionary<string, int> ScaleValues = new(StringComparer.Ordinal)
    {
        ["thousand"] = 1_000,
        ["million"] = 1_000_000
    };

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var normalizedWords = words.Select(NumberWordNormalizer.NormalizeWord).ToArray();
        var index = 0;
        var isNegative = false;

        if (normalizedWords[index] is "minus" or "negative")
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

        if (index < normalizedWords.Length && normalizedWords[index] == "point")
        {
            var decimalPart = ParseDecimalDigits(normalizedWords, index + 1);
            if (decimalPart.Digits.Length > 0)
            {
                replacement += "." + decimalPart.Digits;
                index = decimalPart.NextIndex;
            }
        }

        if (isNegative)
            replacement = "-" + replacement;

        return new NumberWordNormalizer.ParsedWords(replacement, index);
    }

    private static (int Value, int NextIndex)? ParseInteger(IReadOnlyList<string> words, int startIndex)
    {
        var group = ParseGroup(words, startIndex);
        if (group is null)
            return null;

        var total = 0;
        var current = group.Value.Value;
        var index = group.Value.NextIndex;
        var consumedScale = false;

        while (index < words.Count)
        {
            if (!ScaleValues.TryGetValue(words[index], out var scale))
                break;

            total += current * scale;
            current = 0;
            consumedScale = true;
            index++;

            if (index < words.Count && words[index] == "and")
                index++;

            var nextGroup = ParseGroup(words, index);
            if (nextGroup is not null)
            {
                group = nextGroup;
                current = group.Value.Value;
                index = group.Value.NextIndex;
            }
        }

        return (consumedScale ? total + current : current, index);
    }

    private static (int Value, int NextIndex)? ParseGroup(IReadOnlyList<string> words, int startIndex)
    {
        if (startIndex >= words.Count)
            return null;

        var index = startIndex;
        var value = 0;
        var consumed = false;

        if (SmallNumberValue(words[index]) is { } baseValue &&
            index + 1 < words.Count &&
            words[index + 1] == "hundred")
        {
            value = baseValue * 100;
            index += 2;
            consumed = true;

            if (index < words.Count && words[index] == "and")
                index++;
        }

        if (index < words.Count && TensValues.TryGetValue(words[index], out var tens))
        {
            value += tens;
            index++;
            consumed = true;

            if (index < words.Count &&
                UnitValues.TryGetValue(words[index], out var unit) &&
                unit > 0)
            {
                value += unit;
                index++;
            }
        }
        else if (index < words.Count && SmallNumberValue(words[index]) is { } small)
        {
            value += small;
            index++;
            consumed = true;
        }

        return consumed ? (value, index) : null;
    }

    private static (string Digits, int NextIndex) ParseDecimalDigits(IReadOnlyList<string> words, int startIndex)
    {
        var digits = "";
        var index = startIndex;

        while (index < words.Count &&
               UnitValues.TryGetValue(words[index], out var digit) &&
               digit is >= 0 and <= 9)
        {
            digits += digit.ToString();
            index++;
        }

        return (digits, index);
    }

    private static int? SmallNumberValue(string word) =>
        UnitValues.TryGetValue(word, out var unit)
            ? unit
            : TeenValues.TryGetValue(word, out var teen)
                ? teen
                : null;
}
