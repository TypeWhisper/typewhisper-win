using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenAi;

internal static class OpenAiTranscriptionClient
{
    internal static async Task<PluginTranscriptionResult> TranscribeAsync(
        HttpClient httpClient,
        string baseUrl,
        string apiKey,
        string model,
        byte[] wavAudio,
        IReadOnlyList<string> languageHints,
        string? responseFormat,
        string? prompt,
        CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavAudio);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");
        content.Add(new StringContent(model), "model");
        if (!string.IsNullOrWhiteSpace(responseFormat))
            content.Add(new StringContent(responseFormat), "response_format");

        foreach (var languageHint in languageHints)
            content.Add(new StringContent(languageHint), "languages[]");

        if (!string.IsNullOrWhiteSpace(prompt))
            content.Add(new StringContent(prompt), "prompt");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = content;

        using var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseTranscriptionResponse(json);
    }

    internal static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textElement)
            ? textElement.GetString() ?? ""
            : "";
        var language = ParseDetectedLanguage(root);
        var duration = root.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            ? durationElement.GetDouble()
            : 0;

        var segments = new List<PluginTranscriptionSegment>();
        float? minNoSpeechProbability = null;
        if (root.TryGetProperty("segments", out var segmentsElement)
            && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsElement.EnumerateArray())
            {
                var segmentText = segment.TryGetProperty("text", out var segmentTextElement)
                    ? segmentTextElement.GetString() ?? ""
                    : "";
                var start = segment.TryGetProperty("start", out var startElement)
                    && startElement.ValueKind == JsonValueKind.Number
                    ? startElement.GetDouble()
                    : 0;
                var end = segment.TryGetProperty("end", out var endElement)
                    && endElement.ValueKind == JsonValueKind.Number
                    ? endElement.GetDouble()
                    : 0;
                segments.Add(new PluginTranscriptionSegment(segmentText, start, end));

                if (segment.TryGetProperty("no_speech_prob", out var probabilityElement)
                    && probabilityElement.ValueKind == JsonValueKind.Number)
                {
                    var probability = (float)probabilityElement.GetDouble();
                    minNoSpeechProbability = minNoSpeechProbability is null
                        ? probability
                        : Math.Min(minNoSpeechProbability.Value, probability);
                }
            }
        }

        return new PluginTranscriptionResult(
            text.Trim(),
            language,
            duration,
            minNoSpeechProbability)
        {
            Segments = segments
        };
    }

    private static string? ParseDetectedLanguage(JsonElement root)
    {
        if (root.TryGetProperty("language", out var languageElement)
            && languageElement.ValueKind == JsonValueKind.String)
        {
            return languageElement.GetString();
        }

        if (!root.TryGetProperty("languages", out var languagesElement)
            || languagesElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var detectedLanguage in languagesElement.EnumerateArray())
        {
            if (detectedLanguage.ValueKind == JsonValueKind.String)
                return detectedLanguage.GetString();
            if (detectedLanguage.ValueKind == JsonValueKind.Object
                && detectedLanguage.TryGetProperty("code", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.String)
            {
                return codeElement.GetString();
            }
        }

        return null;
    }
}
