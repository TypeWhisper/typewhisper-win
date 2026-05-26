using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Static helper for OpenAI-compatible chat completion API calls.
/// Extracted from CloudProviderBase for reuse by LLM provider plugins.
/// </summary>
public static class OpenAiChatHelper
{
    /// <summary>
    /// Sends a chat completion request to an OpenAI-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "gpt-4o").</param>
    /// <param name="systemPrompt">System prompt text.</param>
    /// <param name="userText">User message text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The assistant's response content text.</returns>
    public static Task<string> SendChatCompletionAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, string systemPrompt, string userText, CancellationToken ct) =>
        SendChatCompletionAsync(
            httpClient,
            baseUrl,
            apiKey,
            model,
            systemPrompt,
            userText,
            ct,
            maxOutputTokens: 2048,
            maxOutputTokenParameter: "max_tokens",
            reasoningEffort: null,
            temperature: 0.1);

    /// <summary>
    /// Sends a chat completion request to an OpenAI-compatible API endpoint.
    /// </summary>
    /// <param name="httpClient">HTTP client to use for the request.</param>
    /// <param name="baseUrl">API base URL (e.g. "https://api.openai.com").</param>
    /// <param name="apiKey">Bearer token for authentication.</param>
    /// <param name="model">Model identifier (e.g. "gpt-4o").</param>
    /// <param name="systemPrompt">System prompt text.</param>
    /// <param name="userText">User message text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxOutputTokens">Optional response token cap.</param>
    /// <param name="maxOutputTokenParameter">Provider-specific token cap parameter name.</param>
    /// <param name="reasoningEffort">Optional reasoning effort value for providers that support it.</param>
    /// <param name="temperature">Optional sampling temperature.</param>
    /// <returns>The assistant's response content text.</returns>
    public static async Task<string> SendChatCompletionAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, string systemPrompt, string userText, CancellationToken ct,
        int? maxOutputTokens = 2048,
        string maxOutputTokenParameter = "max_tokens",
        string? reasoningEffort = null,
        double? temperature = 0.1)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            }
        };

        if (temperature is not null)
            body["temperature"] = temperature.Value;

        if (maxOutputTokens is not null)
            body[maxOutputTokenParameter] = maxOutputTokens.Value;

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning_effort"] = reasoningEffort;

        var requestBody = JsonSerializer.Serialize(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseChatCompletionResponse(json);
    }

    /// <summary>
    /// Parses an OpenAI chat completion JSON response and returns the content of the first choice.
    /// </summary>
    internal static string ParseChatCompletionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
            {
                return content.GetString()?.Trim() ?? "";
            }
        }

        return "";
    }
}
