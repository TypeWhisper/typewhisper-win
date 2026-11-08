using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.Voxtral;

/// <summary>
/// Rewrites Mistral error bodies into the <c>{"error":{"message":…}}</c> shape the SDK error handling reads, so users see
/// Mistral's sentence instead of raw JSON. Only the message is kept: validation errors also echo request fields.
/// </summary>
internal sealed class MistralErrorHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaximumMessageLength = 300;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode) return response;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (Message(body) is not { } message) return response;
        var original = response.Content;
        response.Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json");
        original.Dispose();
        return response;
    }

    /// <summary>Reads <c>message</c> as text, or the <c>msg</c> entries of a validation error's <c>detail</c> list.</summary>
    internal static string? Message(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var message = root.TryGetProperty("message", out var value) ? value : root;
            var text = message.ValueKind == JsonValueKind.String ? message.GetString()
                : message.ValueKind == JsonValueKind.Object && message.TryGetProperty("detail", out var detail) ? Detail(detail)
                : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            return text.Length > MaximumMessageLength ? text[..MaximumMessageLength] : text;
        }
        catch (JsonException) { return null; }
    }

    private static string? Detail(JsonElement detail) => detail.ValueKind switch
    {
        JsonValueKind.String => detail.GetString(),
        JsonValueKind.Array => string.Join("; ", detail.EnumerateArray()
            .Select(item => ProviderConnection.Text(item, "msg")?.Replace("Assertion failed, ", "", StringComparison.Ordinal))
            .Where(text => !string.IsNullOrWhiteSpace(text))),
        _ => null
    };
}
