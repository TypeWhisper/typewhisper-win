namespace TypeWhisper.Core.Services;

/// <summary>Why a failed provider request may be sent again, as far as the caller could classify it.</summary>
/// <param name="RetryAfter">The wait the provider asked for, when its response carried one.</param>
/// <param name="StatusCode">The HTTP status when a response was received.</param>
/// <param name="Kind">A short label for the failure, for the content-free diagnostic log.</param>
public sealed record TransientRequestFailure(TimeSpan? RetryAfter = null, int? StatusCode = null, string? Kind = null);

/// <summary>One retry that is about to happen.</summary>
/// <param name="Attempt">The attempt that failed, counted from one.</param>
/// <param name="Delay">The wait before the next attempt.</param>
/// <param name="Failure">The classification of the failed attempt.</param>
/// <param name="Error">The exception the attempt threw.</param>
public sealed record TransientRequestRetryAttempt(int Attempt, TimeSpan Delay, TransientRequestFailure Failure, Exception Error);

/// <summary>
/// Sends an idempotent request again after a transient failure: at most two retries, after the provider's
/// own wait when it is short enough, otherwise after one and then two seconds.
/// </summary>
/// <remarks>
/// Only whole requests whose repetition is harmless belong here: uploads that are transcribed as a unit
/// and chat completions. Actions with side effects and streams that already delivered partial output
/// must not be retried, so the host wraps those call sites with nothing. A provider that asks for a
/// wait above <see cref="RetryAfterCap"/> is overloaded for longer than a dictation should wait, so
/// its error is surfaced at once instead.
/// </remarks>
public sealed class TransientRequestRetry
{
    /// <summary>How often a request is sent again after its first failure.</summary>
    public const int MaximumRetries = 2;

    /// <summary>The longest provider-requested wait that is honoured.</summary>
    public static readonly TimeSpan RetryAfterCap = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];
    private readonly Func<Exception, TransientRequestFailure?> _classify;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Action<TransientRequestRetryAttempt>? _onRetry;

    /// <summary>Creates a policy whose classification decides which failures are transient.</summary>
    /// <param name="classify">Returns the failure metadata for a retryable exception, or null to surface it.</param>
    /// <param name="delay">Waits before the next attempt; tests pass one that returns at once.</param>
    /// <param name="onRetry">Receives each retry before its wait, for logging.</param>
    public TransientRequestRetry(Func<Exception, TransientRequestFailure?> classify,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action<TransientRequestRetryAttempt>? onRetry = null)
    {
        _classify = classify;
        _delay = delay ?? Task.Delay;
        _onRetry = onRetry;
    }

    /// <summary>Runs the request and sends it again after transient failures until it succeeds or the retries are used up.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled before an attempt or during a wait.</exception>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await request(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // A request that failed because the caller gave up is not a provider failure.
                if (cancellationToken.IsCancellationRequested || attempt > MaximumRetries
                    || _classify(ex) is not { } failure || DelayFor(failure, attempt) is not { } delay) throw;
                _onRetry?.Invoke(new(attempt, delay, failure, ex));
                await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The wait after the given failed attempt, or null when the provider asked for more than the cap.</summary>
    public static TimeSpan? DelayFor(TransientRequestFailure failure, int attempt)
    {
        if (failure.RetryAfter is { } retryAfter)
            return retryAfter <= RetryAfterCap ? (retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter) : null;
        return Backoff[Math.Clamp(attempt, 1, Backoff.Length) - 1];
    }
}
