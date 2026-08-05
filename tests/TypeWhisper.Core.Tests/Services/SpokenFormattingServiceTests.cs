using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;

namespace TypeWhisper.Core.Tests.Services;

public class SpokenFormattingServiceTests
{
    private readonly SpokenFormattingRulesLoader _rulesLoader = new();
    private readonly SpokenFormattingService _sut;

    public SpokenFormattingServiceTests()
    {
        _sut = new SpokenFormattingService(_rulesLoader);
    }

    [Theory]
    [InlineData("de", "Hallo Komma Welt", "Hallo, Welt")]
    [InlineData("de", "Wie geht es dir Fragezeichen", "Wie geht es dir?")]
    [InlineData("de", "Titel Doppelpunkt Beispiel", "Titel: Beispiel")]
    [InlineData("de", "Hallo offene Klammer Test geschlossene Klammer", "Hallo (Test)")]
    [InlineData("en", "Hello comma world", "Hello, world")]
    [InlineData("en", "How are you question mark", "How are you?")]
    [InlineData("en", "Title colon example", "Title: example")]
    [InlineData("en", "Hello open bracket test close bracket", "Hello (test)")]
    public void Normalize_Automatic_ReplacesVisiblePunctuationCommands(
        string language,
        string input,
        string expected)
    {
        var result = _sut.Normalize(input, language, SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("de", "Hallo neue Zeile Welt", "Hallo\nWelt")]
    [InlineData("de", "Hallo neuer Absatz Welt", "Hallo\n\nWelt")]
    [InlineData("de", "Name Tabulator Wert", "Name\tWert")]
    [InlineData("en", "Hello new line world", "Hello\nworld")]
    [InlineData("en", "Hello new paragraph world", "Hello\n\nworld")]
    [InlineData("en", "Name tab value", "Name\tvalue")]
    public void Normalize_Automatic_ReplacesStructuralCommands(
        string language,
        string input,
        string expected)
    {
        var result = _sut.Normalize(input, language, SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Neue Zeile.", "\n")]
    [InlineData("New paragraph!", "\n\n")]
    public void Normalize_StructuralCommand_RemovesDirectlyAttachedAsrPunctuation(string input, string expected)
    {
        var language = input.StartsWith("New", StringComparison.Ordinal) ? "en" : "de";

        var result = _sut.Normalize(input, language, SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_StructuralCommand_PreservesExplicitSpokenPunctuation()
    {
        var result = _sut.Normalize(
            "Hallo neue Zeile Punkt",
            "de",
            SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal("Hallo\n.", result);
    }

    [Fact]
    public void Normalize_CommandOnlyParagraph_PreservesTrailingLineBreaks()
    {
        var result = _sut.Normalize(
            "neuer Absatz",
            "de",
            SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal("\n\n", result);
    }

    [Fact]
    public void Normalize_AutomaticWithoutVisibleCommand_ReturnsExactInput()
    {
        const string input = "  Native output, with  spacing.\r\n";

        var result = _sut.Normalize(input, "en", SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Same(input, result);
    }

    [Fact]
    public void Normalize_FallbackWithoutVisibleCommand_NormalizesSpacing()
    {
        var result = _sut.Normalize(
            "Hello  ,  world",
            "en",
            SpokenFormattingApplicationMode.FullFallback);

        Assert.Equal("Hello, world", result);
    }

    [Theory]
    [InlineData("Hallo Komma, Welt", "Hallo, Welt")]
    [InlineData("Hello question mark?", "Hello?")]
    public void Normalize_DuplicateNativePunctuation_IsSuppressed(string input, string expected)
    {
        var language = input.StartsWith("Hello", StringComparison.Ordinal) ? "en" : "de";

        var result = _sut.Normalize(input, language, SpokenFormattingApplicationMode.SelectiveFallback);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_UnsupportedLanguage_ReturnsExactInput()
    {
        const string input = "Bonjour virgule monde";

        var result = _sut.Normalize(input, "fr-FR", SpokenFormattingApplicationMode.FullFallback);

        Assert.Same(input, result);
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("en_US", "en")]
    [InlineData("  DE  ", "de")]
    [InlineData("auto", null)]
    [InlineData("", null)]
    public void LanguageNormalizer_ReturnsPrimarySupportedCode(string input, string? expected)
    {
        Assert.Equal(expected, SpokenFormattingLanguageNormalizer.Normalize(input));
    }

    [Fact]
    public void RulesLoader_LoadsGermanAndEnglishVerificationScenarios()
    {
        Assert.NotEmpty(_rulesLoader.RuleSetFor("de-DE")!.VerificationScenarios);
        Assert.NotEmpty(_rulesLoader.RuleSetFor("en-US")!.VerificationScenarios);
        Assert.Null(_rulesLoader.RuleSetFor("fr"));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Normalize_AppliesEveryEmbeddedRule(string language)
    {
        var rules = _rulesLoader.RuleSetFor(language)!.Rules;

        foreach (var rule in rules)
        {
            Assert.Equal(
                rule.Replacement,
                _sut.Normalize(rule.Phrase, language, SpokenFormattingApplicationMode.SelectiveFallback));
        }
    }

    [Fact]
    public void Normalize_IsCaseInsensitiveAndUsesUnicodeWordBoundaries()
    {
        Assert.Equal(
            "Hallo, WELT",
            _sut.Normalize("Hallo KOMMA WELT", "de", SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "äkommaß",
            _sut.Normalize("äkommaß", "de", SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "a\u0301komma",
            _sut.Normalize("a\u0301komma", "de", SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "(,)",
            _sut.Normalize("(Komma)", "de", SpokenFormattingApplicationMode.SelectiveFallback));
    }

    [Theory]
    [InlineData("Ende neue Zeile", "Ende\n")]
    [InlineData("Ende neuer Absatz", "Ende\n\n")]
    [InlineData("Ende Tabulator", "Ende\t")]
    public void Normalize_PreservesTrailingStructuralWhitespace(string input, string expected)
    {
        Assert.Equal(
            expected,
            _sut.Normalize(input, "de", SpokenFormattingApplicationMode.SelectiveFallback));
    }

    [Fact]
    public void Normalize_CleansBracketSpacingAndRepeatedNativePunctuation()
    {
        Assert.Equal(
            "Hallo (Test).",
            _sut.Normalize(
                "Hallo offene Klammer Test geschlossene Klammer Punkt",
                "de",
                SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "Hello, world",
            _sut.Normalize("Hello, comma world", "en", SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "Hallo.",
            _sut.Normalize("Hallo Punkt Punkt", "de", SpokenFormattingApplicationMode.SelectiveFallback));
        Assert.Equal(
            "Wait...",
            _sut.Normalize("Wait...", "en", SpokenFormattingApplicationMode.FullFallback));
    }

    [Fact]
    public void RulesLoader_ReturnsTheSameCachedRuleSet()
    {
        Assert.Same(_rulesLoader.RuleSetFor("de"), _rulesLoader.RuleSetFor("de-DE"));
        Assert.Same(_rulesLoader.RuleSetFor("en"), _rulesLoader.RuleSetFor("en_US"));
        Assert.Same(_rulesLoader.RuleSetFor("de"), new SpokenFormattingRulesLoader().RuleSetFor("de"));
    }
}
