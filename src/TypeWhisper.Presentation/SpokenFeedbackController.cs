namespace TypeWhisper.Presentation;

/// <summary>An installed local voice offered by the playback backend.</summary>
/// <param name="Id">Stable system voice identifier.</param>
/// <param name="DisplayName">User-facing installed voice name.</param>
/// <param name="Language">Optional language tag reported by the system.</param>
public sealed record SpokenFeedbackVoice(string Id, string DisplayName, string? Language = null);

/// <summary>One bounded, local speech request. Null voice and output IDs select system defaults.</summary>
/// <param name="Text">Complete text to speak; never silently truncated.</param>
/// <param name="Language">Optional language hint.</param>
/// <param name="VoiceId">Explicit installed voice, or the system default.</param>
/// <param name="OutputDeviceId">Explicit output endpoint, or the system default.</param>
public sealed record SpokenFeedbackRequest(string Text, string? Language = null,
    string? VoiceId = null, string? OutputDeviceId = null)
{
    /// <summary>Maximum accepted UTF-16 text length.</summary>
    public const int MaxTextLength = 4000;
    /// <summary>Maximum synthesized audio size; the backend rejects larger audio before playback.</summary>
    public const int MaxAudioBytes = 12 * 1024 * 1024;
    /// <summary>Maximum synthesized duration; the backend rejects longer audio before playback.</summary>
    public const int MaxAudioSeconds = 120;
}

/// <summary>Local synthesis and playback boundary. Implementations never contact a cloud provider.</summary>
public interface ISpokenFeedbackBackend
{
    /// <summary>Enumerates installed voices without synthesizing or downloading audio.</summary>
    IReadOnlyList<SpokenFeedbackVoice> GetVoices();

    /// <summary>
    /// Synthesizes and plays one request. The returned task completes only after all synthesis,
    /// playback and cancellation work has drained. Enforce request audio bounds before playback;
    /// reject unavailable explicit voices or endpoints instead of silently selecting another.
    /// </summary>
    Task SpeakAsync(SpokenFeedbackRequest request, CancellationToken cancellationToken);
}

/// <summary>Portable admission rules for automatic spoken output.</summary>
public static class SpokenFeedbackPolicy
{
    /// <summary>Only explicitly enabled, successfully processed and delivered final output is spoken.</summary>
    public static bool ShouldSpeakAutomatically(bool enabled, bool processingSucceeded,
        bool deliverySucceeded, bool needsReview) =>
        enabled && processingSucceeded && deliverySucceeded && !needsReview;

    /// <summary>Returns a visible reason for rejecting text, without truncating it.</summary>
    public static string? ValidateText(string? text) => string.IsNullOrWhiteSpace(text)
        ? "There is no text to read aloud."
        : text.Length > SpokenFeedbackRequest.MaxTextLength
            ? $"Spoken feedback supports up to {SpokenFeedbackRequest.MaxTextLength} characters. Your text is unchanged."
            : null;
}

/// <summary>The terminal state of an explicit playback request.</summary>
public enum SpokenFeedbackStatus
{
    /// <summary>All requested audio finished playing.</summary>
    Completed,
    /// <summary>The request was canceled, including an ignored late backend result.</summary>
    Canceled,
    /// <summary>The request was not admitted.</summary>
    Rejected,
    /// <summary>Synthesis or playback failed.</summary>
    Failed
}

/// <summary>A playback outcome with an optional user-facing explanation.</summary>
/// <param name="Status">Terminal request state.</param>
/// <param name="Message">Optional explanation; contains no transcript text.</param>
public sealed record SpokenFeedbackResult(SpokenFeedbackStatus Status, string? Message = null);

/// <summary>
/// Owns one local playback operation. Calls are explicit: this controller never observes history,
/// starts recordings, copies text or retries. Cancellation blocks new admission until backend drain.
/// </summary>
public sealed class SpokenFeedbackController(ISpokenFeedbackBackend backend)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task<SpokenFeedbackResult>? _operation;
    private bool _closed;
    private long _generation;
    private SpokenFeedbackRequest? _activeRequest;

    /// <summary>Whether synthesis, playback or cancellation drain is still running.</summary>
    public bool IsBusy { get { lock (_sync) return _operation is not null; } }
    /// <summary>Whether shutdown has permanently closed admission.</summary>
    public bool IsShutdown { get { lock (_sync) return _closed; } }

    /// <summary>Starts explicit playback, rejecting overlap and invalid input without backend calls.</summary>
    public Task<SpokenFeedbackResult> SpeakAsync(SpokenFeedbackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        TaskCompletionSource<SpokenFeedbackResult> completion;
        CancellationTokenSource cancellation;
        long generation;
        lock (_sync)
        {
            var error = _closed ? "Spoken feedback is shutting down."
                : _operation is not null ? "Spoken feedback is still playing or stopping."
                : SpokenFeedbackPolicy.ValidateText(request.Text);
            if (error is not null)
                return Task.FromResult(new SpokenFeedbackResult(SpokenFeedbackStatus.Rejected, error));
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = new();
            _cancellation = cancellation;
            _operation = completion.Task;
            _activeRequest = request;
            generation = ++_generation;
        }
        _ = RunAsync(request, generation, cancellation, completion);
        return completion.Task;
    }

    /// <summary>Cancels current playback and awaits backend drain; the controller remains reusable.</summary>
    public Task CancelAndDrainAsync() => Stop(false);

    /// <summary>Stops only this exact request; stale view cleanup cannot cancel a newer caller's speech.</summary>
    public Task CancelAndDrainAsync(SpokenFeedbackRequest request) => Stop(false, request);

    /// <summary>Permanently rejects new requests, cancels playback and awaits backend drain.</summary>
    public Task ShutdownAsync() => Stop(true);

    private Task Stop(bool close, SpokenFeedbackRequest? expectedRequest = null)
    {
        CancellationTokenSource? cancellation;
        Task? operation;
        lock (_sync)
        {
            if (expectedRequest is not null && !ReferenceEquals(expectedRequest, _activeRequest)) return Task.CompletedTask;
            _closed |= close;
            ++_generation;
            cancellation = _cancellation;
            operation = _operation;
        }
        // Never invoke arbitrary backend cancellation callbacks while holding the state lock.
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Still await the owned backend task before releasing admission. */ }
        return operation ?? Task.CompletedTask;
    }

    private async Task RunAsync(SpokenFeedbackRequest request, long generation,
        CancellationTokenSource cancellation, TaskCompletionSource<SpokenFeedbackResult> completion)
    {
        SpokenFeedbackResult result;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await backend.SpeakAsync(request, cancellation.Token).ConfigureAwait(false);
            result = new(SpokenFeedbackStatus.Completed);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { result = new(SpokenFeedbackStatus.Canceled, "Spoken feedback stopped."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { result = new(SpokenFeedbackStatus.Failed, "Spoken feedback could not be played. Check the selected voice and audio output."); }
        lock (_sync)
        {
            if (_generation != generation || cancellation.IsCancellationRequested)
                result = new(SpokenFeedbackStatus.Canceled, "Spoken feedback stopped.");
            _operation = null;
            _activeRequest = null;
            _cancellation = null;
            completion.TrySetResult(result);
        }
        cancellation.Dispose();
    }
}
