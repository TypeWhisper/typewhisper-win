// Snapshot of the shared transcription protocol at 3282e9ca; optional authentication is specific to this provider.
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAiCompatible;

/// <summary>
/// Represents caller-supplied audio upload data for Whisper-compatible APIs.
/// </summary>
/// <param name="Data">Encoded audio bytes supplied to the multipart file field.</param>
/// <param name="FileName">File name reported in the multipart file field.</param>
/// <param name="ContentType">MIME content type for the encoded audio.</param>
public sealed record OpenAiTranscriptionUpload(byte[] Data, string FileName, string ContentType);

/// <summary>
/// Static helper for Whisper-compatible audio transcription API calls.
/// Extracted from CloudProviderBase for reuse by transcription engine plugins.
/// </summary>
internal static class CompatibleTranscriptionHelper
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
    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, OpenAiTranscriptionUpload upload, string? language, bool translate,
        string responseFormat, CancellationToken ct, string? prompt = null, Uri? endpointOverride = null)
    {
        ArgumentNullException.ThrowIfNull(upload);

        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(upload.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(upload.ContentType);
        content.Add(fileContent, "file", upload.FileName);
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointOverride ?? new Uri(endpoint));
        OpenAiCompatiblePlugin.Authenticate(request, apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    /// <summary>
    /// Parses a Whisper-compatible JSON transcription response.
    /// </summary>
    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new PluginRequestException("The provider returned invalid transcription JSON.", PluginRequestFailureKind.OutputIncomplete, innerException: ex); }
        using var doc = parsed;
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
            throw new PluginRequestException("The provider returned no valid transcription text.", PluginRequestFailureKind.OutputIncomplete);
        var text = textEl.GetString()!;
        var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String ? langEl.GetString() : null;
        var duration = Number(root, "duration") ?? 0;

        var segments = new List<PluginTranscriptionSegment>();

        // Extract min no_speech_prob from segments (verbose_json format).
        // Using min so that the filter only triggers when ALL segments are silence.
        float? minNoSpeechProb = null;
        if (root.TryGetProperty("segments", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object) continue;
                var segmentText = seg.TryGetProperty("text", out var segTextEl) && segTextEl.ValueKind == JsonValueKind.String
                    ? segTextEl.GetString() ?? ""
                    : "";
                var start = Number(seg, "start") ?? 0;
                var end = Number(seg, "end") ?? 0;
                segments.Add(new PluginTranscriptionSegment(segmentText, start, end));

                if (Number(seg, "no_speech_prob") is { } noSpeech && noSpeech is >= 0 and <= 1)
                {
                    var prob = (float)noSpeech;
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

    private static double? Number(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
}
