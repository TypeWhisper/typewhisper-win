using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.Shared;

// Compiled into the plugins so structured errors do not require a newer SDK helper at runtime.
internal static class TranscriptionHttpErrors
{
    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        try { return await client.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            throw new PluginRequestException("Could not reach the transcription provider.", PluginRequestFailureKind.Network, innerException: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PluginRequestException("The transcription request timed out.", PluginRequestFailureKind.Timeout, innerException: ex);
        }
    }

    internal static PluginRequestException Create(HttpResponseMessage response, string message)
    {
        var status = (int)response.StatusCode;
        var kind = status switch
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
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryAt)
            retryAfter = retryAt - DateTimeOffset.UtcNow;
        if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        return new(message, kind, status, retryAfter);
    }
}
