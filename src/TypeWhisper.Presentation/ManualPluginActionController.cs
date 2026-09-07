namespace TypeWhisper.Presentation;

/// <summary>The observable result of one explicit manual action request.</summary>
public enum ManualPluginActionStatus
{
    /// <summary>The action confirmed successful completion.</summary>
    Succeeded,
    /// <summary>The action explicitly reported a failure, or admission was refused.</summary>
    Failed,
    /// <summary>Cancellation prevented invocation.</summary>
    Canceled,
    /// <summary>Invocation began without a reliable completion result; the destination must be checked before retrying.</summary>
    CompletionUnknown
}

/// <summary>A user-facing result, without automatic URL activation or retry behavior.</summary>
public sealed record ManualPluginActionOutcome(ManualPluginActionStatus Status, string Message);

/// <summary>
/// Thread-safe admission, cancellation and drain for manual plugin actions. The supplied callback must bind
/// a captured registry action identity. Event subscribers dispatch to their UI thread; observer failures cannot block drain.
/// </summary>
public sealed class ManualPluginActionController
{
    private readonly object _sync = new();
    private CancellationTokenSource? _request;
    private Task _cancellationCallbacks = Task.CompletedTask;
    private Task<ManualPluginActionOutcome> _completion = Task.FromResult(new ManualPluginActionOutcome(ManualPluginActionStatus.Canceled, "No action requested."));
    private bool _closed;
    private ManualPluginActionOutcome? _result;

    /// <summary>Raised when admission or completion changes.</summary>
    public event Action? Changed;
    /// <summary>Whether an admitted invocation is still running or draining.</summary>
    public bool IsRunning { get { lock (_sync) return !_completion.IsCompleted; } }
    /// <summary>Whether shutdown permanently closed admission.</summary>
    public bool IsClosing { get { lock (_sync) return _closed; } }
    /// <summary>The latest admitted request's result, or null while it is running.</summary>
    public ManualPluginActionOutcome? Result { get { lock (_sync) return _result; } }
    /// <summary>The currently published request completion, including cancellation callback drain.</summary>
    public Task<ManualPluginActionOutcome> Completion { get { lock (_sync) return _completion; } }

    /// <summary>Runs once when idle. Concurrent requests are refused rather than queued; text is captured at admission.</summary>
    public Task<ManualPluginActionOutcome> ExecuteAsync(string text,
        Func<string, CancellationToken, Task<ManualPluginActionOutcome>> invoke)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(invoke);
        TaskCompletionSource<ManualPluginActionOutcome> completion;
        CancellationTokenSource request;
        lock (_sync)
        {
            if (_closed || !_completion.IsCompleted)
                return Task.FromResult(new ManualPluginActionOutcome(ManualPluginActionStatus.Failed,
                    _closed ? "This review is closing." : "An action is already running. No additional action was queued."));
            request = _request = new();
            _result = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
        }
        _ = ExecuteCoreAsync(text, invoke, request, completion);
        return completion.Task;
    }

    /// <summary>Signals cancellation immediately without claiming that started side effects were undone.</summary>
    public void RequestCancel()
    {
        lock (_sync)
            if (_request is not null) _cancellationCallbacks = _request.CancelAsync();
    }

    /// <summary>Permanently closes admission, requests cancellation and drains the outstanding request.</summary>
    public Task ShutdownAsync()
    {
        lock (_sync)
        {
            _closed = true;
            if (_request is not null) _cancellationCallbacks = _request.CancelAsync();
            return _completion;
        }
    }

    private async Task ExecuteCoreAsync(string text, Func<string, CancellationToken, Task<ManualPluginActionOutcome>> invoke,
        CancellationTokenSource request, TaskCompletionSource<ManualPluginActionOutcome> completion)
    {
        var started = false;
        ManualPluginActionOutcome outcome;
        Exception? fatal = null;
        try
        {
            Notify();
            request.Token.ThrowIfCancellationRequested();
            started = true;
            outcome = await invoke(text, request.Token).ConfigureAwait(false)
                ?? new(ManualPluginActionStatus.CompletionUnknown, "The action returned no completion result. Check its destination before trying again.");
        }
        catch (OperationCanceledException) when (!started)
        { outcome = new(ManualPluginActionStatus.Canceled, "Canceled before the action started."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { outcome = new(ManualPluginActionStatus.CompletionUnknown, "The action's completion could not be confirmed. Check its destination before trying again."); }
        catch (Exception ex)
        {
            fatal = ex;
            outcome = new(ManualPluginActionStatus.CompletionUnknown, "The action could not finish. Check its destination before trying again.");
        }
        Task callbacks;
        lock (_sync) { callbacks = _cancellationCallbacks; _request = null; }
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Manual action cancellation callback failed: " + ex.GetType().Name); }
        lock (_sync)
        {
            _result = outcome;
            _request = null;
            _cancellationCallbacks = Task.CompletedTask;
            request.Dispose();
        }
        if (fatal is not null) completion.TrySetException(fatal);
        else completion.TrySetResult(outcome);
        Notify();
    }

    private void Notify()
    {
        foreach (Action observer in Changed?.GetInvocationList() ?? [])
        {
            try { observer(); }
            catch (Exception ex)
            { System.Diagnostics.Trace.WriteLine("Manual action observer failed: " + ex.GetType().Name); }
        }
    }
}
