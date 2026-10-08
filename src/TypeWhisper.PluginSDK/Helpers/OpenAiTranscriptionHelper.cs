using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Represents caller-supplied audio upload data for Whisper-compatible APIs.
/// </summary>
/// <param name="Data">Encoded audio bytes supplied to the multipart file field.</param>
/// <param name="FileName">File name reported in the multipart file field.</param>
/// <param name="ContentType">MIME content type for the encoded audio.</param>
public sealed record OpenAiTranscriptionUpload(byte[] Data, string FileName, string ContentType);

/// <summary>
/// Provider-specific deviations from the OpenAI multipart transcription request. The defaults reproduce the
/// request <see cref="OpenAiTranscriptionHelper"/> has always sent, so callers that pass nothing keep their
/// current wire format. Plugins used to copy the whole helper to change one of these details.
/// </summary>
public sealed record OpenAiTranscriptionRequestOptions
{
    /// <summary>Options that reproduce the historical request shape.</summary>
    public static OpenAiTranscriptionRequestOptions Default { get; } = new();

    /// <summary>
    /// Leaves the <c>language</c> field out of translation requests. Translation always targets English, and
    /// providers such as Groq reject a source-language hint on that endpoint.
    /// </summary>
    public bool OmitLanguageWhenTranslating { get; init; }

    /// <summary>
    /// Multipart field name that carries the audio. OpenAI reads <c>file</c>; a few compatible servers expect
    /// another name such as <c>audio</c>.
    /// </summary>
    public string FileFieldName { get; init; } = "file";

    /// <summary>
    /// Sends the scalar fields before the audio part. Cohere's endpoint only honours <c>model</c> and the other
    /// scalars when they precede the file; OpenAI accepts either order.
    /// </summary>
    public bool ScalarFieldsFirst { get; init; }

    /// <summary>
    /// Request URL that replaces the <c>/v1/audio/...</c> path derived from the base URL, for deployment-scoped
    /// endpoints such as Azure OpenAI.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Adds the provider's authentication headers instead of the default bearer token, for providers that use an
    /// <c>api-key</c> header or local servers that accept anonymous requests.
    /// </summary>
    public Action<HttpRequestMessage>? Authenticate { get; init; }
}

/// <summary>
/// Static helper for Whisper-compatible audio transcription API calls.
/// Extracted from CloudProviderBase for reuse by transcription engine plugins.
/// </summary>
public static class OpenAiTranscriptionHelper
{
    /// <summary>
    /// Sends a transcription request to a Whisper-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "whisper-1").</param>
    /// <param name="wavAudio">WAV-encoded audio bytes.</param>
    /// <param name="language">Language hint (ISO code) or null for auto-detection.</param>
    /// <param name="translate">If true, uses the translations endpoint (audio to English).</param>
    /// <param name="responseFormat">Response format (e.g. "verbose_json", "json", "text").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcription result with text, detected language, and duration.</returns>
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, byte[] wavAudio, string? language, bool translate,
        string responseFormat, CancellationToken ct) =>
        TranscribeAsync(
            httpClient, baseUrl, apiKey, model, wavAudio, language, translate,
            responseFormat, ct, prompt: null);

    /// <summary>
    /// Transcribes PCM audio using the selected provider configuration.
    /// </summary>
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, byte[] wavAudio, string? language, bool translate,
        string responseFormat, CancellationToken ct, string? prompt = null) =>
        TranscribeAsync(
            httpClient,
            baseUrl,
            apiKey,
            model,
            new OpenAiTranscriptionUpload(wavAudio, "audio.wav", "audio/wav"),
            language,
            translate,
            responseFormat,
            ct,
            prompt);

    /// <summary>
    /// Transcribes caller-supplied encoded audio using the selected provider configuration.
    /// </summary>
    public static Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, OpenAiTranscriptionUpload upload, string? language, bool translate,
        string responseFormat, CancellationToken ct, string? prompt = null) =>
        TranscribeAsync(
            httpClient, baseUrl, apiKey, model, upload, language, translate,
            responseFormat, prompt, OpenAiTranscriptionRequestOptions.Default, ct);

    /// <summary>
    /// Transcribes caller-supplied encoded audio with provider-specific request options.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com"); ignored when <paramref name="options"/> names an endpoint.</param>
    /// <param name="apiKey">Bearer token for authentication; ignored when <paramref name="options"/> authenticates itself.</param>
    /// <param name="model">Model identifier (e.g. "whisper-1").</param>
    /// <param name="upload">Encoded audio and its multipart file metadata.</param>
    /// <param name="language">Language hint (ISO code) or null for auto-detection.</param>
    /// <param name="translate">If true, uses the translations endpoint (audio to English).</param>
    /// <param name="responseFormat">Response format (e.g. "verbose_json"), or null to let the provider choose.</param>
    /// <param name="prompt">Optional prompt or dictionary terms for the model.</param>
    /// <param name="options">Deviations from the OpenAI request shape.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transcription result with text, detected language, and duration.</returns>
    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, OpenAiTranscriptionUpload upload, string? language, bool translate,
        string? responseFormat, string? prompt, OpenAiTranscriptionRequestOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        using var request = CreateRequest(baseUrl, apiKey, model, upload, language, translate, responseFormat, prompt, options);
        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    /// <summary>
    /// Builds the multipart transcription request without sending it, for plugins that own their transport
    /// (custom timeouts, response buffering, retries) but want the shared request shape and
    /// <see cref="ParseTranscriptionResponse"/>.
    /// </summary>
    /// <param name="baseUrl">API base URL; ignored when <paramref name="options"/> names an endpoint.</param>
    /// <param name="apiKey">Bearer token; ignored when <paramref name="options"/> authenticates itself.</param>
    /// <param name="model">Model identifier.</param>
    /// <param name="upload">Encoded audio and its multipart file metadata.</param>
    /// <param name="language">Language hint (ISO code) or null for auto-detection.</param>
    /// <param name="translate">If true, targets the translations endpoint.</param>
    /// <param name="responseFormat">Response format, or null to omit the field.</param>
    /// <param name="prompt">Optional prompt; blank values are omitted.</param>
    /// <param name="options">Deviations from the OpenAI request shape.</param>
    /// <returns>A request the caller owns and must dispose.</returns>
    public static HttpRequestMessage CreateRequest(
        string baseUrl, string apiKey, string model, OpenAiTranscriptionUpload upload,
        string? language, bool translate, string? responseFormat, string? prompt,
        OpenAiTranscriptionRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(options);

        var endpoint = options.Endpoint ?? new Uri(translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions");

        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(upload.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        if (!options.ScalarFieldsFirst)
            content.Add(fileContent, options.FileFieldName, upload.FileName);

        content.Add(new StringContent(model), "model");
        if (responseFormat is not null)
            content.Add(new StringContent(responseFormat), "response_format");

        if (!(translate && options.OmitLanguageWhenTranslating)
            && !string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        if (options.ScalarFieldsFirst)
            content.Add(fileContent, options.FileFieldName, upload.FileName);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        if (options.Authenticate is { } authenticate)
            authenticate(request);
        else
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    /// <summary>
    /// Parses a Whisper-compatible JSON transcription response in <c>json</c> or <c>verbose_json</c> format.
    /// Each segment keeps its own <c>no_speech_prob</c>, and the result carries the minimum across segments so a
    /// silence filter only triggers when every segment looks like silence. Public so plugins that build their own
    /// request body do not have to copy the parser.
    /// </summary>
    /// <param name="json">Response body returned by the provider.</param>
    /// <returns>The transcription with text, detected language, duration, segments and no-speech probability.</returns>
    public static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
        var language = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : null;
        var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetDouble() : 0;

        var segments = new List<PluginTranscriptionSegment>();

        // Extract min no_speech_prob from segments (verbose_json format).
        // Using min so that the filter only triggers when ALL segments are silence.
        float? minNoSpeechProb = null;
        if (root.TryGetProperty("segments", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var segmentText = seg.TryGetProperty("text", out var segTextEl)
                    ? segTextEl.GetString() ?? ""
                    : "";
                var start = seg.TryGetProperty("start", out var startEl)
                    ? startEl.GetDouble()
                    : 0;
                var end = seg.TryGetProperty("end", out var endEl)
                    ? endEl.GetDouble()
                    : 0;
                float? noSpeechProb = seg.TryGetProperty("no_speech_prob", out var nspEl)
                    && nspEl.ValueKind == JsonValueKind.Number
                    ? (float)nspEl.GetDouble()
                    : null;
                segments.Add(new PluginTranscriptionSegment(segmentText, start, end) { NoSpeechProbability = noSpeechProb });

                if (noSpeechProb is { } prob)
                {
                    minNoSpeechProb = minNoSpeechProb is null
                        ? prob
                        : Math.Min(minNoSpeechProb.Value, prob);
                }
            }
        }

        return new PluginTranscriptionResult(text.Trim(), language, duration, minNoSpeechProb)
        {
            Segments = segments
        };
    }
}
