using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TypeWhisper.PluginSDK.Helpers;
using Xunit;

public sealed class OpenAiTranscriptionHelperTests
{
    private static readonly OpenAiTranscriptionUpload Upload = new([1, 2, 3], "audio.m4a", "audio/mp4");

    [Fact]
    public async Task DefaultOptionsKeepTheHistoricalRequestShape()
    {
        var (request, body) = await Send(language: "de", translate: true, options: OpenAiTranscriptionRequestOptions.Default);

        Assert.Equal("https://api.example.com/v1/audio/translations", request.Uri);
        Assert.Equal("Bearer key", request.Authorization);
        var fields = Fields(body);
        Assert.Equal(["file", "model", "response_format", "language", "prompt"], fields.Keys);
        Assert.Equal("de", fields["language"]);
        Assert.Equal("verbose_json", fields["response_format"]);
        Assert.Matches("filename=\"?audio\\.m4a\"?", body);
        Assert.Contains("Content-Type: audio/mp4", body);
    }

    [Fact]
    public async Task OmitLanguageWhenTranslatingDropsOnlyTheTranslationHint()
    {
        var options = new OpenAiTranscriptionRequestOptions { OmitLanguageWhenTranslating = true };

        var (_, translated) = await Send(language: "de", translate: true, options: options);
        var (_, transcribed) = await Send(language: "de", translate: false, options: options);

        Assert.DoesNotContain("language", Fields(translated).Keys);
        Assert.Equal("de", Fields(transcribed)["language"]);
    }

    [Fact]
    public async Task ScalarFieldsFirstAndFileFieldNameFollowTheProviderContract()
    {
        var options = new OpenAiTranscriptionRequestOptions { ScalarFieldsFirst = true, FileFieldName = "audio" };

        var (_, body) = await Send(language: null, translate: false, options: options, responseFormat: null, prompt: " ");

        Assert.Equal(["model", "audio"], Fields(body).Keys);
    }

    [Fact]
    public async Task EndpointAndAuthenticateReplaceTheDerivedUrlAndBearerToken()
    {
        var options = new OpenAiTranscriptionRequestOptions
        {
            Endpoint = new Uri("https://deployment.example.com/openai/deployments/whisper/audio/transcriptions?api-version=1"),
            Authenticate = request => request.Headers.TryAddWithoutValidation("api-key", "secret"),
        };

        var (request, _) = await Send(language: null, translate: false, options: options);

        Assert.Equal(options.Endpoint.AbsoluteUri, request.Uri);
        Assert.Null(request.Authorization);
        Assert.Equal("secret", request.ApiKeyHeader);
    }

    [Fact]
    public void ParsesSegmentsWithTheirOwnNoSpeechProbability()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""
            {"text":" Hello there. Thank you. ","language":"en","duration":4.5,
             "segments":[
               {"text":" Hello there.","start":0,"end":2,"no_speech_prob":0.1},
               {"text":" Thank you.","start":2,"end":4.5,"no_speech_prob":0.9},
               {"text":" (no probability)","start":4.5,"end":4.5,"no_speech_prob":null}]}
            """);

        Assert.Equal("Hello there. Thank you.", result.Text);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(4.5, result.DurationSeconds);
        Assert.Equal(0.1f, result.NoSpeechProbability);
        Assert.Equal([0.1f, 0.9f, null], result.Segments.Select(segment => segment.NoSpeechProbability));
        Assert.Equal(" Thank you.", result.Segments[1].Text);
    }

    [Fact]
    public void ParsesPlainJsonWithoutSegments()
    {
        var result = OpenAiTranscriptionHelper.ParseTranscriptionResponse("""{"text":"Hello"}""");

        Assert.Equal("Hello", result.Text);
        Assert.Null(result.DetectedLanguage);
        Assert.Equal(0, result.DurationSeconds);
        Assert.Null(result.NoSpeechProbability);
        Assert.Empty(result.Segments);
    }

    private static async Task<(Captured Request, string Body)> Send(string? language, bool translate,
        OpenAiTranscriptionRequestOptions options, string? responseFormat = "verbose_json", string? prompt = "terms")
    {
        Captured? captured = null;
        string? body = null;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            body = await request.Content!.ReadAsStringAsync(ct);
            captured = new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"ok"}""", Encoding.UTF8, "application/json")
            };
        }));

        var result = await OpenAiTranscriptionHelper.TranscribeAsync(client, "https://api.example.com", "key", "whisper-1",
            Upload, language, translate, responseFormat, prompt, options, CancellationToken.None);

        Assert.Equal("ok", result.Text);
        return (captured!, body!);
    }

    // Multipart field names in the order they were written, with the scalar values.
    private static Dictionary<string, string> Fields(string body)
    {
        var fields = new Dictionary<string, string>();
        var parts = body.Split("\r\n\r\n");
        for (var index = 0; index + 1 < parts.Length; index++)
        {
            var header = parts[index].Split("\r\n")
                .LastOrDefault(line => line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase));
            if (header is null) continue;
            var disposition = ContentDispositionHeaderValue.Parse(header[(header.IndexOf(':') + 1)..].Trim());
            var name = disposition.Name!.Trim('"');
            fields[name] = parts[index + 1].Split("\r\n")[0];
        }
        return fields;
    }

    private sealed record Captured(string Uri, string? Authorization, string? ApiKeyHeader);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct);
    }
}
