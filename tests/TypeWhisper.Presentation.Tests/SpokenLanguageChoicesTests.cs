using TypeWhisper.Presentation;
using Xunit;

public sealed class SpokenLanguageChoicesTests
{
    private static readonly string[] Canary = ["en", "de", "fr", "es"];

    [Fact]
    public void ModelThatDetectsTheLanguageOffersEveryLanguage()
    {
        var choices = SpokenLanguageChoices.For([]);
        Assert.Contains("de", choices);
        Assert.Contains("en", choices);
        Assert.DoesNotContain("iv", choices);
        Assert.All(choices, code => Assert.Matches("^[a-z]{2,3}$", code));
    }

    [Fact]
    public void ModelWithALanguageListOffersOnlyThatList() =>
        Assert.Equal(Canary, SpokenLanguageChoices.For(Canary));

    [Theory]
    [InlineData(null, "auto")]
    [InlineData("auto", "auto")]
    [InlineData("de", "de")]
    [InlineData("not-a-language", "auto")]
    public void ModelThatDetectsTheLanguageKeepsAChosenLanguage(string? saved, string expected) =>
        Assert.Equal(expected, SpokenLanguageChoices.Resolve([], saved));

    [Theory]
    [InlineData(null, "en")]
    [InlineData("de", "de")]
    [InlineData("ja", "en")]
    [InlineData("auto", "en")]
    public void ModelWithALanguageListFallsBackToEnglish(string? saved, string expected) =>
        Assert.Equal(expected, SpokenLanguageChoices.Resolve(Canary, saved));

    [Fact]
    public void ModelWithALanguageListWithoutEnglishFallsBackToItsFirstLanguage() =>
        Assert.Equal("de", SpokenLanguageChoices.Resolve(["de", "fr"], "ja"));

    [Theory]
    [InlineData("auto", true)]
    [InlineData("de", true)]
    [InlineData("not-a-language", false)]
    public void ModelThatDetectsTheLanguageAcceptsAnyKnownLanguage(string language, bool expected) =>
        Assert.Equal(expected, SpokenLanguageChoices.CanSelect([], language));

    [Theory]
    [InlineData("de", true)]
    [InlineData("auto", false)]
    [InlineData("ja", false)]
    public void ModelWithALanguageListAcceptsOnlyItsLanguages(string language, bool expected) =>
        Assert.Equal(expected, SpokenLanguageChoices.CanSelect(Canary, language));

    [Theory]
    [InlineData("de", "de")]
    [InlineData("auto", null)]
    [InlineData(null, null)]
    public void ModelWithALanguageListReceivesTheChosenLanguage(string? language, string? expected) =>
        Assert.Equal(expected, SpokenLanguageChoices.ForEngine(Canary, language));

    [Fact]
    public void ModelThatDetectsTheLanguageNeverReceivesTheChoice() =>
        Assert.Null(SpokenLanguageChoices.ForEngine([], "de"));

    [Fact]
    public void ChosenLanguageEnablesSpokenCommandsForParakeet()
    {
        var language = SpokenLanguageChoices.Resolve([], "de");
        var preferences = DictationFormatting.WithProfile(new DictationTextPreferences(), "sherpa-onnx", "parakeet-tdt-0.6b", "de",
            TypeWhisper.Core.Models.SpokenFormattingStrategy.Automatic);
        var resolved = DictationFormatting.Resolve(preferences, "sherpa-onnx", "parakeet-tdt-0.6b", language, null);
        Assert.Equal("Hallo\nDanke.", DictationFormatting.Apply("Hallo, neue Zeile, Danke.", resolved));
    }
}
