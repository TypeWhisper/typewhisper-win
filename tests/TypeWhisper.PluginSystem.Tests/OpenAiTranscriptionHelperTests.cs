using System.Net;
using System.Net.Http;
using System.Text;
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
    public async Task TranscribeAsync_CustomUploadPreservesMultipartFieldsAndParsesResponse()
    {
        string? capturedBody = null;
        var handler = new CapturingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.example.test/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());

            capturedBody = await request.Content!.ReadAsStringAsync();
            AssertMultipartToken(capturedBody, "name", "file");
            AssertMultipartToken(capturedBody, "filename", "audio.m4a");
            Assert.Contains("Content-Type: audio/mp4", capturedBody);
            AssertMultipartToken(capturedBody, "name", "model");
            Assert.Contains("whisper-large-v3", capturedBody);
            AssertMultipartToken(capturedBody, "name", "language");
            Assert.Contains("de", capturedBody);
            AssertMultipartToken(capturedBody, "name", "response_format");
            Assert.Contains("verbose_json", capturedBody);
            AssertMultipartToken(capturedBody, "name", "prompt");
            Assert.Contains("Bitte sauber transkribieren", capturedBody);
            Assert.DoesNotContain("audio.wav", capturedBody);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"text":"  hallo welt  ","language":"de","duration":12.5}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var upload = new OpenAiTranscriptionUpload(
            Encoding.UTF8.GetBytes("compressed-bytes"),
            "audio.m4a",
            "audio/mp4");

        var result = await OpenAiTranscriptionHelper.TranscribeAsync(
            httpClient,
            "https://api.example.test",
            "test-key",
            "whisper-large-v3",
            upload,
            "de",
            translate: false,
            "verbose_json",
            CancellationToken.None,
            "Bitte sauber transkribieren");

        Assert.NotNull(capturedBody);
        Assert.Equal("hallo welt", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        Assert.Equal(12.5, result.DurationSeconds);
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

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request);
    }

    private static void AssertMultipartToken(string body, string name, string value)
    {
        Assert.True(
            body.Contains($"{name}=\"{value}\"", StringComparison.Ordinal)
            || body.Contains($"{name}={value}", StringComparison.Ordinal),
            $"Multipart body did not contain {name}={value}:{Environment.NewLine}{body}");
    }
}
