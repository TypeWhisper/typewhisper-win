namespace TypeWhisper.Core.Services.NumberNormalization;

internal static class ChineseNumberWordParser
{
    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words) =>
        HanNumberWordParser.Parse(words);
}

internal static class JapaneseNumberWordParser
{
    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words) =>
        HanNumberWordParser.Parse(words);
}

internal static class HanNumberWordParser
{
    private static readonly Dictionary<char, int> DigitValues = new()
    {
        ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['兩'] = 2, ['三'] = 3,
        ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9
    };

    private static readonly Dictionary<char, int> UnitValues = new()
    {
        ['十'] = 10, ['百'] = 100, ['千'] = 1_000
    };

    private static readonly Dictionary<char, int> LargeUnitValues = new()
    {
        ['万'] = 10_000, ['萬'] = 10_000, ['亿'] = 100_000_000, ['億'] = 100_000_000
    };

    private static readonly HashSet<char> DecimalMarkers = ['点', '點'];
    private static readonly HashSet<char> NegativeMarkers = ['负', '負'];

    public static NumberWordNormalizer.ParsedWords? Parse(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
            return null;

        var consumedWords = 0;
        var pieces = new List<string>();

        if (words[0] == "マイナス")
        {
            pieces.Add("負");
            consumedWords = 1;
        }

        while (consumedWords < words.Count && IsHanNumberWord(words[consumedWords]))
        {
            pieces.Add(words[consumedWords]);
            consumedWords++;
        }

        if (pieces.Count == 0)
            return null;

        var numberText = string.Concat(pieces);
        if (!ContainsNumberMarker(numberText))
            return null;

        var parsed = ParseNumberText(numberText);
        return parsed is null ? null : new NumberWordNormalizer.ParsedWords(parsed, consumedWords);
    }

    private static string? ParseNumberText(string text)
    {
        var characters = text.ToList();
        var isNegative = false;

        if (characters.FirstOrDefault() is { } first && NegativeMarkers.Contains(first))
        {
            isNegative = true;
            characters.RemoveAt(0);
            if (characters.Count == 0)
                return null;
        }

        var parts = SplitDecimal(characters);
        if (!parts.IntegerHadNumber)
            return null;

        var integer = ParseInteger(parts.Integer);
        if (integer is null)
            return null;

        var replacement = integer.Value.ToString();
        if (parts.Decimal is { } decimalPart)
        {
            var decimalDigits = decimalPart
                .Select(c => DigitValues.TryGetValue(c, out var digit) ? digit : (int?)null)
                .ToArray();
            if (decimalDigits.Any(static digit => digit is null) || decimalDigits.Length == 0)
                return null;

            replacement += "." + string.Concat(decimalDigits.Select(static digit => digit!.Value.ToString()));
        }

        return isNegative ? "-" + replacement : replacement;
    }

    private static (List<char> Integer, bool IntegerHadNumber, List<char>? Decimal) SplitDecimal(IReadOnlyList<char> characters)
    {
        var decimalIndex = characters
            .Select((character, index) => new { character, index })
            .FirstOrDefault(item => DecimalMarkers.Contains(item.character))
            ?.index;

        if (decimalIndex is null)
            return (characters.ToList(), characters.Any(IsNumericCharacter), null);

        var integer = characters.Take(decimalIndex.Value).ToList();
        var decimalCharacters = characters.Skip(decimalIndex.Value + 1).ToList();
        return (integer, integer.Any(IsNumericCharacter), decimalCharacters);
    }

    private static int? ParseInteger(IReadOnlyList<char> characters)
    {
        if (characters.Count == 0)
            return null;

        var total = 0;
        var section = 0;
        var number = 0;

        foreach (var character in characters)
        {
            if (DigitValues.TryGetValue(character, out var digit))
            {
                number = digit;
                continue;
            }

            if (UnitValues.TryGetValue(character, out var unit))
            {
                section += (number == 0 ? 1 : number) * unit;
                number = 0;
                continue;
            }

            if (LargeUnitValues.TryGetValue(character, out var largeUnit))
            {
                section += number;
                total += Math.Max(section, 1) * largeUnit;
                section = 0;
                number = 0;
                continue;
            }

            return null;
        }

        return total + section + number;
    }

    private static bool IsHanNumberWord(string word) =>
        word.Length > 0 && word.All(static c => IsNumericCharacter(c) || NegativeMarkers.Contains(c));

    private static bool IsNumericCharacter(char character) =>
        DigitValues.ContainsKey(character) ||
        UnitValues.ContainsKey(character) ||
        LargeUnitValues.ContainsKey(character) ||
        DecimalMarkers.Contains(character);

    private static bool ContainsNumberMarker(string text) =>
        text.Any(static c =>
            NegativeMarkers.Contains(c) ||
            DecimalMarkers.Contains(c) ||
            UnitValues.ContainsKey(c) ||
            LargeUnitValues.ContainsKey(c));
}
