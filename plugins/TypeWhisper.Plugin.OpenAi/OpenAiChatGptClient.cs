using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TypeWhisper.Plugin.OpenAi;

internal sealed class OpenAiChatGptClient
{
    internal const string Endpoint = "https://chatgpt.com/backend-api/codex/responses";

    private readonly HttpClient _httpClient;
    private readonly string _accessToken;
    private readonly string? _accountId;

    public OpenAiChatGptClient(HttpClient httpClient, string accessToken, string? accountId)
    {
        _httpClient = httpClient;
        _accessToken = accessToken;
        _accountId = accountId;
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        string? reasoningEffort,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(_accountId))
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", _accountId);

        request.Content = OpenAiJson.CreateJsonContent(
            CreateRequestBody(model, systemPrompt, userText, reasoningEffort));

        var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ParseErrorMessage(body, (int)response.StatusCode));

        return ParseResponseText(body)
            ?? throw new InvalidOperationException("The ChatGPT response could not be parsed.");
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
            ["stream"] = OpenAiJson.Element(true),
        };

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = OpenAiJson.Element(new { effort = reasoningEffort });

        return body;
    }

    internal static string? ParseResponseText(string body) =>
        ParseJsonResponseText(body) ?? ParseEventStreamResponseText(body);

    private static string? ParseEventStreamResponseText(string body)
    {
        var deltaBuffer = "";
        var completedParts = new List<string>();

        foreach (var rawLine in body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var payload = line[6..];
            if (payload == "[DONE]")
                continue;

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
                continue;

            switch (typeEl.GetString())
            {
                case "response.output_text.delta":
                    if (GetString(root, "delta") is { } delta)
                        deltaBuffer += delta;
                    break;
                case "response.output_text.done":
                    if (GetString(root, "text") is { Length: > 0 } text)
                        completedParts.Add(text);
                    break;
                case "response.content_part.done":
                    if (root.TryGetProperty("part", out var part)
                        && GetString(part, "text") is { Length: > 0 } partText)
                    {
                        completedParts.Add(partText);
                    }
                    break;
            }
        }

        if (!string.IsNullOrEmpty(deltaBuffer))
            return deltaBuffer.Trim();

        var completed = string.Join("\n", completedParts).Trim();
        return string.IsNullOrEmpty(completed) ? null : completed;
    }

    private static string? ParseJsonResponseText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (GetString(root, "output_text") is { Length: > 0 } outputText)
                return outputText.Trim();

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message))
            {
                if (GetString(message, "content") is { Length: > 0 } content)
                    return content.Trim();
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

                    parts.AddRange(content.EnumerateArray()
                        .Select(block => GetString(block, "text"))
                        .Where(text => !string.IsNullOrWhiteSpace(text))!);
                }

                var joined = string.Join("\n", parts).Trim();
                if (!string.IsNullOrEmpty(joined))
                    return joined;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string ParseErrorMessage(string body, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (GetString(root, "detail") is { Length: > 0 } detail)
                return detail;
            if (GetString(root, "message") is { Length: > 0 } message)
                return message;
            if (root.TryGetProperty("error", out var error))
            {
                if (GetString(error, "message") is { Length: > 0 } apiMessage)
                    return apiMessage;
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? $"HTTP {statusCode}";
            }
        }
        catch (JsonException)
        {
            return $"HTTP {statusCode}";
        }

        return $"HTTP {statusCode}";
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
