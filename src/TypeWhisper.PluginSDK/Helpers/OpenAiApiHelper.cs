using System.Net.Http;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Shared HTTP error handling for OpenAI-compatible API calls.
/// </summary>
public static class OpenAiApiHelper
{
    /// <summary>
    /// Sends an HTTP request and handles common API error responses with German error messages.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithErrorHandlingAsync(
        HttpClient httpClient, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Netzwerkfehler: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Zeitüberschreitung bei der API-Anfrage.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var message = (int)response.StatusCode switch
            {
                401 => "Ungültiger API-Key",
                413 => "Audio zu groß (max 25 MB)",
                429 => "Rate-Limit erreicht, bitte warten",
                _ => $"API-Fehler {(int)response.StatusCode}: {ExtractErrorMessage(errorBody)}"
            };
            throw new InvalidOperationException(message);
        }

        return response;
    }

    /// <summary>
    /// Extracts a human-readable error message from an OpenAI-style error JSON body.
    /// Falls back to truncating the raw body if parsing fails.
    /// </summary>
    public static string ExtractErrorMessage(string errorBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                if (errorEl.ValueKind == JsonValueKind.Object && errorEl.TryGetProperty("message", out var msgEl))
                    return msgEl.GetString() ?? errorBody;
                if (errorEl.ValueKind == JsonValueKind.String)
                    return errorEl.GetString() ?? errorBody;
            }
        }
        catch
        {
            // JSON parsing failed, fall through to truncation
        }

        return errorBody.Length > 200 ? errorBody[..200] : errorBody;
    }
}
