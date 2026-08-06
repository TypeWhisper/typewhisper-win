namespace TypeWhisper.PluginSDK;

/// <summary>
/// Describes the reason a plugin-backed provider request failed.
/// </summary>
public enum PluginRequestFailureKind
{
    /// <summary>The failure could not be classified safely.</summary>
    Unknown = 0,
    /// <summary>The provider could not be reached.</summary>
    Network,
    /// <summary>The request timed out.</summary>
    Timeout,
    /// <summary>The provider rejected the request because it was rate limited.</summary>
    RateLimit,
    /// <summary>The provider returned a transient server error.</summary>
    ServerError,
    /// <summary>The provider returned an empty response.</summary>
    EmptyResponse,
    /// <summary>Authentication failed.</summary>
    Authentication,
    /// <summary>The authenticated account lacks permission.</summary>
    Permission,
    /// <summary>The provider is not configured correctly.</summary>
    Configuration,
    /// <summary>The request was too large.</summary>
    RequestTooLarge,
    /// <summary>The provider rejected an invalid or unsupported request.</summary>
    InvalidRequest,
    /// <summary>The request was cancelled.</summary>
    Cancellation
}

/// <summary>
/// Exposes structured provider-request failure metadata to the host.
/// </summary>
public class PluginRequestException : InvalidOperationException
{
    /// <summary>
    /// Creates a structured plugin request exception.
    /// </summary>
    public PluginRequestException(
        string message,
        PluginRequestFailureKind failureKind,
        int? httpStatusCode = null,
        TimeSpan? retryAfter = null,
        bool? isTransient = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
        IsTransient = isTransient ?? failureKind is
            PluginRequestFailureKind.Network
            or PluginRequestFailureKind.Timeout
            or PluginRequestFailureKind.RateLimit
            or PluginRequestFailureKind.ServerError
            or PluginRequestFailureKind.EmptyResponse;
    }

    /// <summary>Gets the classified failure kind.</summary>
    public PluginRequestFailureKind FailureKind { get; }
    /// <summary>Gets the HTTP status code when an HTTP response was received.</summary>
    public int? HttpStatusCode { get; }
    /// <summary>Gets the provider-supplied retry delay when available.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>Gets whether another identical request can reasonably succeed.</summary>
    public bool IsTransient { get; }
}
