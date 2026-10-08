using TypeWhisper.Core.Services;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

/// <summary>
/// The host's one retry policy for cloud provider requests: transcription uploads and chat completions.
/// Plugins classify their failures through <see cref="PluginRequestException"/> but never retry a
/// rate limit or server error themselves; this is where that metadata is consumed.
/// </summary>
public static class PluginRequestRetry
{
    /// <summary>
    /// Whether a failed request may be sent again: the SDK's own transient flag, or the status codes
    /// providers answer with while overloaded even when a plugin classified them differently.
    /// </summary>
    public static TransientRequestFailure? Classify(Exception exception) =>
        exception is PluginRequestException request && (request.IsTransient || request.HttpStatusCode is 429 or 502 or 503 or 504)
            ? new(request.RetryAfter, request.HttpStatusCode, request.FailureKind.ToString())
            : null;

    /// <summary>Creates the policy for one plugin; tests inject a delay that returns at once.</summary>
    public static TransientRequestRetry Create(string? pluginId, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(Classify, delay, retry => PluginHostDiagnostics.Write(
            $"cloud.retry plugin={pluginId} attempt={retry.Attempt} delayMs={(long)retry.Delay.TotalMilliseconds} kind={retry.Failure.Kind}"
            + (retry.Failure.StatusCode is { } status ? $" status={status}" : ""), retry.Error));

    /// <summary>Runs one request whose repetition is harmless under the policy.</summary>
    public static Task<T> RunAsync<T>(string? pluginId, Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken,
        TransientRequestRetry? policy = null) =>
        (policy ?? Create(pluginId)).RunAsync(request, cancellationToken);
}
