using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Shared;

// Cohere part of the connection; the shared core is linked from plugins/shared/ProviderConnection.cs.
internal sealed partial class ProviderConnection
{
    private static partial string NormalizeLanguage(string language) => language;
    private static partial IEnumerable<string> ParseTerms(string? prompt) =>
        prompt?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
    internal async Task<PluginTranscriptionResult> MultipartAsync(string url, string model, byte[] audio, string? language,
        string? prompt, CancellationToken ct, string? key = null, string fileField = "file", string? responseFormat = null)
    {
        using var request = Request(HttpMethod.Post, url, key);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(model), "model");
        if (responseFormat is not null) form.Add(new StringContent(responseFormat), "response_format");
        if (Language(language) is { } lang) form.Add(new StringContent(lang), "language");
        if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");
        // Cohere requires every scalar field before the file part.
        var file = new ByteArrayContent(audio); file.Headers.ContentType = new("audio/wav"); form.Add(file, fileField, "audio.wav");
        request.Content = form;
        using var document = await ReadAsync(request, ct); var root = document.RootElement;
        var text = Text(root, "text") ?? throw InvalidResponse();
        var segments = new List<PluginTranscriptionSegment>(); float? noSpeech = null;
        if (root.TryGetProperty("segments", out var rawSegments) && rawSegments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in rawSegments.EnumerateArray())
            {
                var start = Number(segment, "start"); var end = Number(segment, "end");
                if (end >= start && Text(segment, "text") is { } segmentText) segments.Add(new(segmentText, start, end));
                var probability = Number(segment, "no_speech_prob", -1);
                if (probability is >= 0 and <= 1) noSpeech = noSpeech is null ? (float)probability : Math.Min(noSpeech.Value, (float)probability);
            }
        }
        return new(text.Trim(), Text(root, "language") ?? Language(language), Number(root, "duration"), noSpeech) { Segments = segments };
    }
    internal static bool ValidModelId(string? model) => !string.IsNullOrWhiteSpace(model) && model.Length <= 256
        && !model.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));
    internal async Task<string> ChatAsync(string url, string model, string system, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ValidModelId(model)) throw new ArgumentException("Invalid model ID.");
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content = input } },
            ["max_tokens"] = Math.Min(8192, LlmOutputTokenBudget.CalculateWithReasoningReserve(system, input))
        };
        if (Get("temperatureMode", "providerDefault") == "custom")
        {
            if (!double.TryParse(Get("temperature", "0.3"), NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature)
                || !double.IsFinite(temperature) || temperature < 0 || temperature > 2)
                throw new PluginRequestException("The saved Cohere temperature must be a number from 0 to 2.", PluginRequestFailureKind.Configuration);
            body["temperature"] = temperature;
        }
        using var request = Request(HttpMethod.Post, url); request.Content = Json(body);
        using var document = await ReadAsync(request, ct); var root = document.RootElement;
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "Provider");
        var choices = Required(root, "choices", JsonValueKind.Array);
        if (choices.GetArrayLength() == 0 || Text(choices[0], "finish_reason") != "stop") throw InvalidResponse();
        return RequiredText(Required(choices[0], "message", JsonValueKind.Object), "content").Trim();
    }
}
