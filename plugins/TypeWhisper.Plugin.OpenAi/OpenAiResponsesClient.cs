using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiResponsesClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <summary>
    /// Performs open ai responses client.
    /// </summary>
    public OpenAiResponsesClient(HttpClient httpClient, string baseUrl, string apiKey)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    /// <summary>
    /// Processes input text with the selected provider configuration.
    /// </summary>
    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        string? reasoningEffort,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = OpenAiJson.CreateJsonContent(
            CreateRequestBody(model, systemPrompt, userText, reasoningEffort));

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    internal static Dictionary<string, JsonElement> CreateRequestBody(
        string model,
        string systemPrompt,
        string userText,
        string? reasoningEffort)
    {
        var instructions = string.IsNullOrWhiteSpace(systemPrompt)
            ? "You are a helpful assistant."
            : systemPrompt;

        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = OpenAiJson.Element(model),
            ["instructions"] = OpenAiJson.Element(instructions),
            ["input"] = OpenAiJson.Element(new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = userText }
                    }
                }
            }),
            ["store"] = OpenAiJson.Element(false),
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = OpenAiJson.Element(new { effort = reasoningEffort });

        return body;
    }

    internal static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            var text = outputText.GetString()?.Trim();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        if (root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    var type = contentItem.TryGetProperty("type", out var typeEl)
                        ? typeEl.GetString()
                        : null;
                    if (type is not null and not "output_text" and not "text")
                        continue;

                    if (contentItem.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String
                        && textEl.GetString() is { } text)
                    {
                        parts.Add(text);
                    }
                }
            }

            var joined = string.Concat(parts).Trim();
            if (!string.IsNullOrEmpty(joined))
                return joined;
        }

        throw new InvalidOperationException("Failed to parse OpenAI response text.");
    }
}
