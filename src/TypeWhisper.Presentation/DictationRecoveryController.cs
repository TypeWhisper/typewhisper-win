using TypeWhisper.Core.Services;

namespace TypeWhisper.Presentation;

/// <summary>A manually accepted recovery result. Creating it performs no delivery or usage actions.</summary>
public sealed record DictationRecoveryReview(string RecordingId, string Text);

/// <summary>Serializes explicit recovery actions. The host owns the audio store and must drain this controller before disposing it.</summary>
public sealed class DictationRecoveryController
{
    private readonly DictationRecoveryAudioStore _store;
    private readonly Func<string, CancellationToken, Task<string>> _transcribe;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private bool _busy;
    private bool _closed;
    /// <summary>Creates a controller without loading, deleting, or transcribing audio.</summary>
    public DictationRecoveryController(DictationRecoveryAudioStore store,
        Func<string, CancellationToken, Task<string>> transcribe)
    { _store = store; _transcribe = transcribe; }
    /// <summary>Published audio descriptors, without arbitrary source paths.</summary>
    public IReadOnlyList<RecoveryRecordingDescriptor> Recordings => _store.Recordings;
    /// <summary>The last accepted result, retained if a later retry fails or is canceled.</summary>
    public DictationRecoveryReview? Review { get; private set; }
    /// <summary>Current status or failure, safe to display.</summary>
    public string? Message { get; private set; }
    /// <summary>Whether an explicit operation is still draining.</summary>
    public bool Busy { get { lock (_sync) return _busy; } }
    /// <summary>Whether shutdown has closed admission permanently.</summary>
    public bool IsShutdown { get { lock (_sync) return _closed; } }
    /// <summary>Raised on state changes; consumers must dispatch to their UI thread.</summary>
    public event Action? Changed;
    /// <summary>Refreshes descriptors without transcribing or delivering anything.</summary>
    public Task RefreshAsync() => Run(async token =>
    {
        await _store.InitializeAsync(token).ConfigureAwait(false);
        await _store.RefreshAsync(token).ConfigureAwait(false);
        Message = _store.LastError ?? "Choose an audio recording to transcribe and review. Nothing runs automatically.";
    });
    /// <summary>Transcribes one internally enumerated source; cancellation rejects even a decoder that returns late.</summary>
    public Task RetryAsync(string id) => Run(async token =>
    {
        var path = _store.GetRecordingPath(id) ?? throw new IOException("Recovery source unavailable.");
        var result = await _transcribe(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("No text was returned.");
        lock (_sync)
        {
            if (_closed || token.IsCancellationRequested) return;
            Review = new(id, result);
            Message = "Review ready. Audio is still saved. Nothing was pasted or added to History.";
        }
    });
    /// <summary>Deletes only the confirmed identifier; the UI must obtain explicit confirmation before calling.</summary>
    public Task DeleteConfirmedAsync(string id) => Run(async token =>
    {
        token.ThrowIfCancellationRequested();
        if (!await _store.DeleteAsync(id, token).ConfigureAwait(false))
            throw new IOException("Recovery audio could not be deleted.");
        Message = "Recovery audio deleted. Your reviewed text is still available.";
    });
    /// <summary>Requests cancellation without releasing the source or admitting overlapping work.</summary>
    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) cancellation = _cancellation;
        Cancel(cancellation);
    }
    private void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        catch (AggregateException) { Message = "Recovery cancellation was requested, but a decoder callback failed. Waiting for it to finish."; Notify(); }
    }
    /// <summary>Closes admission, cancels current work, and awaits the actual decoder completion.</summary>
    public Task ShutdownAsync()
    {
        Task operation;
        lock (_sync) { _closed = true; operation = _operation; }
        Cancel(); return operation;
    }
    /// <summary>Cancels and drains current work while keeping the controller reusable after navigation.</summary>
    public Task CancelAndDrainAsync()
    {
        Task operation;
        CancellationTokenSource? cancellation;
        lock (_sync) { operation = _operation; cancellation = _cancellation; }
        Cancel(cancellation); return operation;
    }
    private Task Run(Func<CancellationToken, Task> action)
    {
        TaskCompletionSource completion;
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_closed || _busy) return Task.CompletedTask;
            _busy = true;
            cancellation = _cancellation = new();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _operation = completion.Task;
        }
        _ = Execute();
        return completion.Task;
        async Task Execute()
        {
            Notify();
            try { cancellation.Token.ThrowIfCancellationRequested(); await action(cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            { Message = "Recovery canceled. Audio and any previous review were kept."; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { Message = cancellation.IsCancellationRequested ? "Recovery canceled. Audio and any previous review were kept."
                : "The recovery action failed. Audio that could not be deleted and any previous review were kept. Retry when the source and model are available."; }
            finally
            {
                lock (_sync) { _busy = false; _cancellation = null; }
                cancellation.Dispose();
                Notify(); completion.TrySetResult();
            }
        }
    }
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
