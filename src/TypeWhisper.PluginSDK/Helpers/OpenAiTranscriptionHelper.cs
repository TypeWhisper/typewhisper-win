using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSDK.Helpers;

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

    public static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, byte[] wavAudio, string? language, bool translate,
        string responseFormat, CancellationToken ct, string? prompt = null)
    {
        var endpoint = translate
            ? $"{baseUrl}/v1/audio/translations"
            : $"{baseUrl}/v1/audio/transcriptions";

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrEmpty(language) && language != "auto")
            content.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    /// <summary>
    /// Parses a Whisper-compatible JSON transcription response.
    /// </summary>
    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
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
                segments.Add(new PluginTranscriptionSegment(segmentText, start, end));

                if (seg.TryGetProperty("no_speech_prob", out var nspEl))
                {
                    var prob = (float)nspEl.GetDouble();
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
