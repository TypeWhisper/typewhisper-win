using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Rejects provider responses that explicitly report an incomplete token-limited result.
/// </summary>
public static class LlmResponseTruncationGuard
{
    private static readonly string[] TokenLimitReasons =
    [
        "length",
        "max_tokens",
        "max_output_tokens",
        "model_context_window_exceeded"
    ];

    /// <summary>Rejects a token-limited OpenAI-compatible chat completion.</summary>
    public static void ThrowIfOpenAiChatCompletionTruncated(
        JsonElement root,
        string providerName)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return;
        }

        var firstChoice = choices[0];
        if (IsTokenLimitReason(GetString(firstChoice, "finish_reason"))
            || IsTokenLimitReason(GetString(firstChoice, "native_finish_reason")))
        {
            Throw(providerName);
        }
    }

    /// <summary>Rejects a token-limited Anthropic message response.</summary>
    public static void ThrowIfAnthropicResponseTruncated(
        JsonElement root,
        string providerName)
    {
        if (IsTokenLimitReason(GetString(root, "stop_reason")))
            Throw(providerName);
    }

    /// <summary>Rejects an incomplete Responses API result or completion event.</summary>
    public static void ThrowIfResponsesApiIncomplete(
        JsonElement root,
        string providerName)
    {
        if (root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object)
        {
            ThrowIfResponsesApiIncomplete(response, providerName);
        }

        var status = GetString(root, "status");
        if (string.Equals(status, "incomplete", StringComparison.OrdinalIgnoreCase))
            Throw(providerName);

        if (root.TryGetProperty("incomplete_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && IsTokenLimitReason(GetString(details, "reason")))
        {
            Throw(providerName);
        }
    }

    private static bool IsTokenLimitReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && TokenLimitReasons.Contains(reason, StringComparer.OrdinalIgnoreCase);

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static void Throw(string providerName) =>
        throw new PluginRequestException(
            $"{providerName} stopped the workflow response at its token limit.",
            PluginRequestFailureKind.OutputTruncated,
            isTransient: false);
}
