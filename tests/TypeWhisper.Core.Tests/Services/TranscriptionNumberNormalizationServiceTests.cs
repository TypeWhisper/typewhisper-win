using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.NumberNormalization;

namespace TypeWhisper.Core.Tests.Services;

public class TranscriptionNumberNormalizationServiceTests
{
    [Fact]
    public void NormalizeText_DefaultEnabled_UsesDetectedLanguage()
    {
        var result = TranscriptionNumberNormalizationService.NormalizeText(
            "I have two questions",
            TranscriptionTask.Transcribe,
            detectedLanguage: "en",
            configuredLanguage: null,
            configuredLanguageCandidates: []);

        Assert.Equal("I have 2 questions", result);
    }

    [Fact]
    public void NormalizeText_GlobalOff_SkipsNormalization()
    {
        var result = TranscriptionNumberNormalizationService.NormalizeText(
            "I have two questions",
            TranscriptionTask.Transcribe,
            detectedLanguage: "en",
            configuredLanguage: null,
            configuredLanguageCandidates: [],
            globalEnabled: false);

        Assert.Equal("I have two questions", result);
    }

    [Fact]
    public void NormalizeText_OverrideOff_WinsOverGlobalOn()
    {
        var result = TranscriptionNumberNormalizationService.NormalizeText(
            "I have two questions",
            TranscriptionTask.Transcribe,
            detectedLanguage: "en",
            configuredLanguage: null,
            configuredLanguageCandidates: [],
            normalizeNumbersOverride: false);

        Assert.Equal("I have two questions", result);
    }

    [Fact]
    public void NormalizeText_LaterLanguageCandidateCanNormalize()
    {
        var result = TranscriptionNumberNormalizationService.NormalizeText(
            "Set the value to twenty three",
            TranscriptionTask.Transcribe,
            detectedLanguage: "de",
            configuredLanguage: "de",
            configuredLanguageCandidates: ["de", "en"]);

        Assert.Equal("Set the value to 23", result);
    }

    [Fact]
    public void NormalizeResult_NormalizesTextAndSegments()
    {
        var transcription = new TranscriptionResult
        {
            Text = "two",
            DetectedLanguage = "en",
            Duration = 1.25,
            ProcessingTime = 0.2,
            Segments = [new TranscriptionSegment("two", 0, 1.25)]
        };

        var result = TranscriptionNumberNormalizationService.NormalizeResult(
            transcription,
            TranscriptionTask.Transcribe,
            configuredLanguage: null,
            configuredLanguageCandidates: []);

        Assert.Equal("2", result.Text);
        Assert.Equal("2", result.Segments.Single().Text);
        Assert.Equal(0, result.Segments.Single().Start);
        Assert.Equal(1.25, result.Segments.Single().End);
    }
}
