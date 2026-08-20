using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class EnglishOutputNormalizationServiceTests
{
    [Fact]
    public void NormalizeText_AmericanEnglish_ConvertsCommonBritishSpellingsAndPreservesCase()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "Colour, ANALYSE, Recognised and travelling.",
            EnglishOutputVariant.UnitedStates,
            detectedLanguage: "en-GB");

        Assert.Equal("Color, ANALYZE, Recognized and traveling.", result);
    }

    [Fact]
    public void NormalizeText_BritishEnglish_ConvertsCommonAmericanSpellings()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "The color was analyzed in the center while traveling.",
            EnglishOutputVariant.UnitedKingdom,
            detectedLanguage: "en-US");

        Assert.Equal("The colour was analysed in the centre while travelling.", result);
    }

    [Theory]
    [InlineData(EnglishOutputVariant.AsTranscribed)]
    [InlineData((EnglishOutputVariant)999)]
    public void NormalizeText_AsTranscribed_PreservesSpelling(EnglishOutputVariant variant)
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "The colour was analysed.",
            variant,
            detectedLanguage: "en");

        Assert.Equal("The colour was analysed.", result);
    }

    [Fact]
    public void NormalizeText_KnownNonEnglishOutput_PreservesText()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "Die Farbe heißt Colour.",
            EnglishOutputVariant.UnitedStates,
            detectedLanguage: "de");

        Assert.Equal("Die Farbe heißt Colour.", result);
    }

    [Fact]
    public void NormalizeText_WithoutEnglishLanguageSelection_PreservesText()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "The colour was analysed.",
            EnglishOutputVariant.UnitedStates);

        Assert.Equal("The colour was analysed.", result);
    }

    [Fact]
    public void NormalizeText_TranslateTaskWithoutTarget_NormalizesEnglishOutput()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "The colour was analysed.",
            EnglishOutputVariant.UnitedStates,
            TranscriptionTask.Translate,
            detectedLanguage: "de");

        Assert.Equal("The color was analyzed.", result);
    }

    [Fact]
    public void NormalizeText_EnglishTranslationTarget_NormalizesTranslatedOutput()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "The colour was analysed.",
            EnglishOutputVariant.UnitedStates,
            detectedLanguage: "de",
            translationTarget: "en-US");

        Assert.Equal("The color was analyzed.", result);
    }

    [Fact]
    public void NormalizeText_NonEnglishTranslationTarget_WinsOverEnglishSource()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "Die Farbe heißt Colour.",
            EnglishOutputVariant.UnitedStates,
            detectedLanguage: "en",
            translationTarget: "de");

        Assert.Equal("Die Farbe heißt Colour.", result);
    }

    [Fact]
    public void NormalizeText_BritishEnglish_PreservesAmbiguousAmericanSpellings()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "Practice the computer program.",
            EnglishOutputVariant.UnitedKingdom,
            detectedLanguage: "en");

        Assert.Equal("Practice the computer program.", result);
    }

    [Fact]
    public void NormalizeText_MixedCaseWord_PreservesPotentialBrandSpelling()
    {
        var result = EnglishOutputNormalizationService.NormalizeText(
            "CoLoUr",
            EnglishOutputVariant.UnitedStates,
            detectedLanguage: "en");

        Assert.Equal("CoLoUr", result);
    }

    [Fact]
    public void NormalizeResult_NormalizesTextAndSegments()
    {
        var transcription = new TranscriptionResult
        {
            Text = "The colour was analysed.",
            DetectedLanguage = "en",
            Segments = [new TranscriptionSegment("The colour was analysed.", 0.25, 1.75)]
        };

        var result = EnglishOutputNormalizationService.NormalizeResult(
            transcription,
            EnglishOutputVariant.UnitedStates);

        Assert.Equal("The color was analyzed.", result.Text);
        var segment = Assert.Single(result.Segments);
        Assert.Equal("The color was analyzed.", segment.Text);
        Assert.Equal(0.25, segment.Start);
        Assert.Equal(1.75, segment.End);
    }
}
