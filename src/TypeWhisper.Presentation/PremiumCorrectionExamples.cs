namespace TypeWhisper.Presentation;

/// <summary>What a learned correction typically fixes.</summary>
public enum PremiumCorrectionKind
{
    /// <summary>A typo the recognition keeps producing.</summary>
    Typo,
    /// <summary>A name the recognition does not know.</summary>
    Name,
    /// <summary>The spelling of a product or a technical term.</summary>
    Spelling
}

/// <summary>One correction example: what was recognized, what the user typed instead, and what kind of fix it is.</summary>
public sealed record PremiumCorrectionExample(string Before, string After, PremiumCorrectionKind Kind);

/// <summary>Correction examples for the Premium page in the interface language, as on macOS.</summary>
public static class PremiumCorrectionExamples
{
    /// <summary>A typo, a name and a spelling for the language; English for every other language.</summary>
    public static IReadOnlyList<PremiumCorrectionExample> For(string language)
    {
        var code = (language ?? "").ToLowerInvariant();
        if (code.StartsWith("de", StringComparison.Ordinal))
            return [new("Standart", "Standard", PremiumCorrectionKind.Typo), new("Prija", "Priya", PremiumCorrectionKind.Name), new("Type Whisper", "TypeWhisper", PremiumCorrectionKind.Spelling)];
        if (code.StartsWith("ja", StringComparison.Ordinal))
            return [new("こんにちわ", "こんにちは", PremiumCorrectionKind.Typo), new("斎藤", "齋藤", PremiumCorrectionKind.Name), new("タイプウィスパー", "TypeWhisper", PremiumCorrectionKind.Spelling)];
        if (code.StartsWith("zh", StringComparison.Ordinal))
            return [new("因该", "应该", PremiumCorrectionKind.Typo), new("李华", "李桦", PremiumCorrectionKind.Name), new("Type Whisper", "TypeWhisper", PremiumCorrectionKind.Spelling)];
        return [new("teh", "the", PremiumCorrectionKind.Typo), new("Pria", "Priya", PremiumCorrectionKind.Name), new("Type Whisper", "TypeWhisper", PremiumCorrectionKind.Spelling)];
    }

    /// <summary>The typo example as one line, for a status or preview.</summary>
    public static string Primary(string language) => Arrow(For(language)[0]);

    /// <summary>The typo and the name example on one line, separated by a middle dot.</summary>
    public static string Line(string language) => string.Join("  ·  ", For(language).Take(2).Select(Arrow));

    /// <summary>"before → after".</summary>
    public static string Arrow(PremiumCorrectionExample example) => example.Before + " → " + example.After;
}
