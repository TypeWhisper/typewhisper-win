using System.Net.Http;
using System.Text.Json;

using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Helpers;

namespace TypeWhisper.Plugin.OpenAi;

/// <summary>
/// Shared HTTP error handling for OpenAI-compatible API calls.
/// </summary>
internal static class OpenAiApiTransport
{
    /// <summary>
    /// Sends an HTTP request and handles common API error responses.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithErrorHandlingAsync(
        HttpClient httpClient, HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            try { await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, timeout.Token); }
            catch { response.Dispose(); throw; }
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException(
                "OpenAI network request failed.",
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
                _ => $"OpenAI API error {statusCode}."
            };
            // Preserve the narrow audio-format retry without exposing provider response content.
            var detailed = new PluginRequestException(ExtractErrorMessage(errorBody), PluginRequestFailureKind.InvalidRequest, statusCode);
            if (OpenAiPlugin.ShouldRetryWithWav(detailed)) message = statusCode == 500
                ? "OpenAI could not decode the audio (ffprobe failed)."
                : "OpenAI rejected the audio format (unsupported audio format).";
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
