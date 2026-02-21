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
    public static async Task<string> SendChatCompletionAsync(
        HttpClient httpClient, string baseUrl, string apiKey,
        string model, string systemPrompt, string userText, CancellationToken ct)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText }
            },
            temperature = 0.1,
            max_tokens = 2048
        });

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
