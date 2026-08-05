using System.Net.Http;
using System.Text.Json;

namespace TypeWhisper.PluginSDK.Helpers;

/// <summary>
/// Shared HTTP error handling for OpenAI-compatible API calls.
/// </summary>
public static class OpenAiApiHelper
{
    /// <summary>
    /// Sends an HTTP request and handles common API error responses.
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
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                $"Network error: {ex.Message}",
                PluginRequestFailureKind.Network,
                innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException(
                "API request timed out.",
                PluginRequestFailureKind.Timeout,
                innerException: ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
                retryAfter = retryAt - DateTimeOffset.UtcNow;
            string errorBody;
            try
            {
                errorBody = await response.Content.ReadAsStringAsync(ct);
            }
            finally
            {
                response.Dispose();
            }
            var message = statusCode switch
            {
                401 => "Invalid API key",
                413 => "Audio too large (max 25 MB)",
                429 => "Rate limit reached, please wait",
                _ => $"API error {statusCode}: {ExtractErrorMessage(errorBody)}"
            };
            var failureKind = statusCode switch
            {
                401 => PluginRequestFailureKind.Authentication,
                403 => PluginRequestFailureKind.Permission,
                408 => PluginRequestFailureKind.Timeout,
                413 => PluginRequestFailureKind.RequestTooLarge,
                429 => PluginRequestFailureKind.RateLimit,
                >= 500 and <= 599 => PluginRequestFailureKind.ServerError,
                >= 400 and <= 499 => PluginRequestFailureKind.InvalidRequest,
                _ => PluginRequestFailureKind.Unknown
            };
            throw new PluginRequestException(
                message,
                failureKind,
                statusCode,
                retryAfter);
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
