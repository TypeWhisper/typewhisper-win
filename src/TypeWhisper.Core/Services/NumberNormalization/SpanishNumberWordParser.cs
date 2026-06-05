namespace TypeWhisper.Core.Services.NumberNormalization;

internal static class SpanishNumberWordParser
{
    private static readonly Dictionary<string, int> UnitValues = new(StringComparer.Ordinal)
    {
        ["cero"] = 0, ["uno"] = 1, ["un"] = 1, ["una"] = 1, ["dos"] = 2, ["tres"] = 3,
        ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8, ["nueve"] = 9
    };

    private static readonly Dictionary<string, int> TeenValues = new(StringComparer.Ordinal)
    {
        ["diez"] = 10, ["once"] = 11, ["doce"] = 12, ["trece"] = 13, ["catorce"] = 14,
        ["quince"] = 15, ["dieciseis"] = 16, ["diecisiete"] = 17, ["dieciocho"] = 18, ["diecinueve"] = 19
    };

    private static readonly Dictionary<string, int> TwentyValues = new(StringComparer.Ordinal)
    {
        ["veinte"] = 20, ["veintiuno"] = 21, ["veintiun"] = 21, ["veintiuna"] = 21,
        ["veintidos"] = 22, ["veintitres"] = 23, ["veinticuatro"] = 24, ["veinticinco"] = 25,
        ["veintiseis"] = 26, ["veintisiete"] = 27, ["veintiocho"] = 28, ["veintinueve"] = 29
    };

    private static readonly Dictionary<string, int> TensValues = new(StringComparer.Ordinal)
    {
        ["treinta"] = 30, ["cuarenta"] = 40, ["cincuenta"] = 50,
        ["sesenta"] = 60, ["setenta"] = 70, ["ochenta"] = 80, ["noventa"] = 90
    };

    private static readonly Dictionary<string, int> HundredValues = new(StringComparer.Ordinal)
    {
        ["cien"] = 100, ["ciento"] = 100, ["doscientos"] = 200, ["doscientas"] = 200,
        ["trescientos"] = 300, ["trescientas"] = 300, ["cuatrocientos"] = 400, ["cuatrocientas"] = 400,
        ["quinientos"] = 500, ["quinientas"] = 500, ["seiscientos"] = 600, ["seiscientas"] = 600,
        ["setecientos"] = 700, ["setecientas"] = 700, ["ochocientos"] = 800, ["ochocientas"] = 800,
        ["novecientos"] = 900, ["novecientas"] = 900
    };

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var normalizedWords = words.Select(NumberWordNormalizer.NormalizeWord).ToArray();
        var index = 0;
        var isNegative = false;

        if (normalizedWords[index] == "menos")
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

        if (index < normalizedWords.Length && normalizedWords[index] == "coma")
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

            if (word is "millon" or "millones")
            {
                total += Math.Max(current, 1) * 1_000_000;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (word == "mil")
            {
                total += Math.Max(current, 1) * 1_000;
                current = 0;
                index++;
                consumed = true;
                lastWasPlainSmallNumber = false;
                continue;
            }

            if (HundredValues.TryGetValue(word, out var hundred))
            {
                current += hundred;
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

        if (word == "veinte")
            return AppendSpanishUnit(20, words, startIndex + 1, allowWithoutY: true);

        if (TwentyValues.TryGetValue(word, out var twenty))
            return (twenty, startIndex + 1);

        if (TensValues.TryGetValue(word, out var ten))
            return AppendSpanishUnit(ten, words, startIndex + 1, allowWithoutY: false);

        if (TeenValues.TryGetValue(word, out var teen))
            return (teen, startIndex + 1);

        if (UnitValue(word, allowArticleOne) is { } unit)
            return (unit, startIndex + 1);

        return null;
    }

    private static (int Value, int NextIndex) AppendSpanishUnit(
        int baseValue,
        IReadOnlyList<string> words,
        int startIndex,
        bool allowWithoutY)
    {
        var index = startIndex;

        if (index < words.Count && words[index] == "y")
        {
            var afterY = index + 1;
            if (afterY < words.Count &&
                UnitValue(words[afterY], true) is { } unit &&
                unit > 0)
            {
                return (baseValue + unit, afterY + 1);
            }

            return (baseValue, startIndex);
        }

        if (allowWithoutY &&
            index < words.Count &&
            UnitValue(words[index], true) is { } unitWithoutY &&
            unitWithoutY > 0)
        {
            return (baseValue + unitWithoutY, index + 1);
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
               words[index + 1] is "mil" or "millon" or "millones";
    }
}
