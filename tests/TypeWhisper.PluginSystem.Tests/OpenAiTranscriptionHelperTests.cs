using System.Net.Http;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.PluginSystem.Tests;

public class OpenAiTranscriptionHelperTests
{
    [Fact]
    public void TranscribeAsync_ExposesLegacyBinaryCompatibleSignature()
    {
        var method = typeof(OpenAiTranscriptionHelper).GetMethod(
            nameof(OpenAiTranscriptionHelper.TranscribeAsync),
            [
                typeof(HttpClient),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(byte[]),
                typeof(string),
                typeof(bool),
                typeof(string),
                typeof(CancellationToken)
            ]);

        Assert.NotNull(method);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ExtractsNoSpeechProb()
    {
        var json = """
        {
            "text": "So.",
            "language": "en",
            "duration": 2.5,
            "segments": [
                { "text": "So.", "no_speech_prob": 0.95 }
            ]
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("So.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.9f);
    }

    [Fact]
    public void ParseTranscriptionResponse_VerboseJson_ReturnsMinNoSpeechProb()
    {
        // Uses min so that mixed speech/silence audio is NOT filtered out
        var json = """
        {
            "text": "Hello world. So.",
            "language": "en",
            "duration": 5.0,
            "segments": [
                { "text": "Hello world.", "no_speech_prob": 0.1 },
                { "text": "So.", "no_speech_prob": 0.92 }
            ]
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.Equal(0.1f, result.NoSpeechProbability.Value, 0.01f);
    }

    [Fact]
    public void ParseTranscriptionResponse_AllSegmentsSilence_ReturnsHighProb()
    {
        var json = """
        {
            "text": "So. Vorsicht!",
            "language": "en",
            "duration": 3.0,
            "segments": [
                { "text": "So.", "no_speech_prob": 0.95 },
                { "text": "Vorsicht!", "no_speech_prob": 0.88 }
            ]
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability > 0.8f);
    }

    [Fact]
    public void ParseTranscriptionResponse_JsonFormat_NoSegments_ReturnsNull()
    {
        var json = """
        {
            "text": "Hello world",
            "language": "en",
            "duration": 2.0
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Equal("Hello world", result.Text);
        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_EmptySegments_ReturnsNull()
    {
        var json = """
        {
            "text": "",
            "language": "en",
            "duration": 1.0,
            "segments": []
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.Null(result.NoSpeechProbability);
    }

    [Fact]
    public void ParseTranscriptionResponse_LowNoSpeechProb_IndicatesSpeech()
    {
        var json = """
        {
            "text": "This is a normal sentence.",
            "language": "en",
            "duration": 3.0,
            "segments": [
                { "text": "This is a normal sentence.", "no_speech_prob": 0.02 }
            ]
        }
        """;

        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse(json);

        Assert.NotNull(result.NoSpeechProbability);
        Assert.True(result.NoSpeechProbability < 0.1f);
    }
}
