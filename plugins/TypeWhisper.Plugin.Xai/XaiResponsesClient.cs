using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.Xai;

internal sealed class XaiResponsesClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public XaiResponsesClient(HttpClient httpClient, string baseUrl, string apiKey)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task<string> ProcessAsync(
        string systemPrompt,
        string userText,
        string model,
        CancellationToken ct)
    {
        var body = new Dictionary<string, JsonElement>
        {
            ["model"] = XaiJson.Element(model),
            ["store"] = XaiJson.Element(false),
            ["input"] = XaiJson.Element(new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userText },
            }),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = XaiJson.CreateJsonContent(body);

        var response = await OpenAiApiHelper.SendWithErrorHandlingAsync(_httpClient, request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseResponse(json);
    }

    public static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (TryGetNonEmptyString(root, "output_text") is { } outputText)
            return outputText;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
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
                    var type = TryGetNonEmptyString(contentItem, "type");
                    if (type is not null
                        && type != "output_text"
                        && type != "text")
                    {
                        continue;
                    }

                    if (TryGetNonEmptyString(contentItem, "text") is { } text)
                        parts.Add(text);
                }
            }

            var nestedText = JoinTextParts(parts);
            if (!string.IsNullOrWhiteSpace(nestedText))
                return nestedText;
        }

        throw new InvalidOperationException("Failed to parse xAI response text.");
    }

    private static string JoinTextParts(IReadOnlyList<string> parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts.Where(static part => !string.IsNullOrEmpty(part)))
        {
            if (builder.Length > 0
                && !char.IsWhiteSpace(builder[builder.Length - 1])
                && !char.IsWhiteSpace(part[0])
                && !char.IsPunctuation(part[0]))
            {
                builder.Append(' ');
            }

            builder.Append(part);
        }

        return builder.ToString().Trim();
    }

    private static string? TryGetNonEmptyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
