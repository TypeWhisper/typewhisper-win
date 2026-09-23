using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.OpenRouter;

public sealed partial class OpenRouterPlugin
{
    // The macOS provider requests timed responses only from these upstreams.
    private static bool SupportsVerboseTimestamps(string model) =>
        model.Split('/')[0] is "openai" or "groq" or "together";

    private static JsonDocument ParseResponse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new PluginRequestException("OpenRouter returned invalid JSON.", PluginRequestFailureKind.OutputIncomplete, innerException: ex);
        }
    }

    private static void ThrowProviderError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PluginRequestException("OpenRouter returned an invalid response.", PluginRequestFailureKind.OutputIncomplete);
        if (!root.TryGetProperty("error", out var error)) return;
        int? code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var codeValue)
            && codeValue.ValueKind == JsonValueKind.Number && codeValue.TryGetInt32(out var number) ? number : null;
        var kind = code switch
        {
            401 => PluginRequestFailureKind.Authentication,
            403 => PluginRequestFailureKind.Permission,
            429 => PluginRequestFailureKind.RateLimit,
            >= 500 => PluginRequestFailureKind.ServerError,
            >= 400 => PluginRequestFailureKind.InvalidRequest,
            _ => PluginRequestFailureKind.OutputIncomplete
        };
        // Do not surface arbitrary provider payloads containing request data.
        throw new PluginRequestException($"OpenRouter returned a provider error{(code is null ? "." : $" ({code}).")}", kind, code);
    }

    private static string ParseChatCompletionResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PluginRequestException("OpenRouter returned no text.", PluginRequestFailureKind.EmptyResponse);
        using var doc = ParseResponse(json);
        var root = doc.RootElement;
        ThrowProviderError(root);
        LlmResponseTruncationGuard.ThrowIfOpenAiChatCompletionTruncated(root, "OpenRouter");
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0 && choices[0].ValueKind == JsonValueKind.Object
            && choices[0].TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content))
        {
            var text = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(content.EnumerateArray()
                    .Where(part => part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "text"
                        && part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    .Select(part => part.GetProperty("text").GetString())),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        throw new PluginRequestException("OpenRouter returned no text.", PluginRequestFailureKind.EmptyResponse);
    }

    private static PluginTranscriptionResult ParseTranscriptionResponse(string json)
    {
        using var doc = ParseResponse(json);
        var root = doc.RootElement;
        ThrowProviderError(root);
        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new PluginRequestException("OpenRouter returned an invalid transcript.", PluginRequestFailureKind.OutputIncomplete);
        var language = root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
        var duration = 0d;
        if (TryReadDouble(root, "duration", out var seconds) && double.IsFinite(seconds) && seconds >= 0) duration = seconds;
        else if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && TryReadDouble(usage, "seconds", out seconds) && double.IsFinite(seconds) && seconds >= 0) duration = seconds;
        var segments = new List<PluginTranscriptionSegment>();
        if (root.TryGetProperty("segments", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in array.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String
                    || !TryReadDouble(segment, "start", out var start) || !TryReadDouble(segment, "end", out var end)
                    || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start) continue;
                segments.Add(new(value.GetString()!, start, end));
            }
        }
        return new(text.GetString()!.Trim(), language, duration, null) { Segments = segments };
    }
}
