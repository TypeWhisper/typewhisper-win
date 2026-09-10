namespace TypeWhisper.Presentation;

/// <summary>An immutable state for one explicit model operation; progress is supplied only by the plugin.</summary>
public sealed record ModelDownloadState(bool IsBusy, bool IsClosing, double? Progress, string? Message, bool Succeeded)
{
    /// <summary>Whether this operation removes model files rather than downloading them.</summary>
    public bool IsRemoval { get; init; }
}

/// <summary>
/// Thread-safe single-operation admission and cancellation drain. Callbacks must await the actual plugin operation.
/// Changed can run on any thread; UI consumers dispatch it. No selection, load or automatic retry occurs.
/// </summary>
public sealed class ModelDownloadController
{
    private readonly object _sync = new();
    private CancellationTokenSource? _request;
    private Task _callbacks = Task.CompletedTask;
    private Task _completion = Task.CompletedTask;
    private ModelDownloadState _state = new(false, false, null, null, false);
    private long _generation;

    /// <summary>Raised after state changes; throwing listeners cannot prevent operation drain.</summary>
    public event Action? Changed;
    /// <summary>Returns the current immutable state.</summary>
    public ModelDownloadState State { get { lock (_sync) return _state; } }
    /// <summary>Returns completion of the admitted operation and cancellation callbacks.</summary>
    public Task Completion { get { lock (_sync) return _completion; } }

    /// <summary>Runs an explicit download, refusing concurrent or shutdown requests without queuing them.</summary>
    public Task RunAsync(Func<IProgress<double>, CancellationToken, Task> download)
    {
        ArgumentNullException.ThrowIfNull(download);
        return RunOperationAsync(download, removal: false);
    }

    /// <summary>Runs one explicitly confirmed removal using the same admission and drain as downloads.</summary>
    public Task RunRemovalAsync(Func<CancellationToken, Task> remove)
    {
        ArgumentNullException.ThrowIfNull(remove);
        return RunOperationAsync((_, token) => remove(token), removal: true);
    }

    private Task RunOperationAsync(Func<IProgress<double>, CancellationToken, Task> operation, bool removal)
    {
        CancellationTokenSource request;
        TaskCompletionSource completion;
        long generation;
        lock (_sync)
        {
            if (_state.IsClosing || !_completion.IsCompleted)
                throw new InvalidOperationException("Finish the current model operation before starting another.");
            request = _request = new();
            generation = ++_generation;
            _state = new(true, false, null, removal ? "Removing model…" : "Downloading model…", false) { IsRemoval = removal };
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
        }
        _ = RunCoreAsync(operation, request, generation, completion, removal);
        return completion.Task;
    }

    /// <summary>Requests cancellation immediately and waits for plugin and callback drain; allows a later retry.</summary>
    public Task CancelAndDrainAsync()
    {
        Task completion;
        lock (_sync)
        {
            ++_generation;
            if (_request is { IsCancellationRequested: false })
            {
                _callbacks = _request.CancelAsync();
                _state = _state with { Message = _state.IsRemoval ? "Canceling model removal…" : "Canceling model download…" };
            }
            completion = _completion;
        }
        Notify();
        return completion;
    }

    /// <summary>Permanently rejects new work, cancels and drains the current request without a release timeout.</summary>
    public Task ShutdownAsync()
    {
        lock (_sync) _state = _state with { IsClosing = true };
        return CancelAndDrainAsync();
    }

    private async Task RunCoreAsync(Func<IProgress<double>, CancellationToken, Task> download,
        CancellationTokenSource request, long generation, TaskCompletionSource completion, bool removal)
    {
        var succeeded = false;
        string message;
        Exception? fatal = null;
        try
        {
            Notify();
            request.Token.ThrowIfCancellationRequested();
            await download(new InlineProgress(value => Report(generation, value)), request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            succeeded = true;
            message = removal ? "Model files removed." : "Model downloaded. Select it explicitly to use it.";
        }
        catch (OperationCanceledException)
        { message = CanceledMessage(removal); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { message = removal ? "The model could not be removed. Refresh model status; some files may have been removed." : "The model could not be downloaded. Check its requirements and configuration before trying again."; }
        catch (Exception ex)
        { fatal = ex; message = removal ? "The model removal could not finish." : "The model download could not finish."; }
        lock (_sync)
            if (succeeded && generation == _generation && !request.IsCancellationRequested)
                _state = _state with { Message = removal ? "Finishing model removal…" : "Finishing model download…" };
        Notify();
        Task callbacks;
        lock (_sync) { callbacks = _callbacks; _request = null; }
        try { await callbacks.ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Model cancellation callback failed: " + ex.GetType().Name); }
        lock (_sync)
        {
            // Cancellation wins until completion is published under this same admission lock.
            if (generation != _generation || request.IsCancellationRequested || _state.IsClosing)
            {
                succeeded = false;
                message = CanceledMessage(removal);
            }
            request.Dispose();
            _callbacks = Task.CompletedTask;
            _state = _state with { IsBusy = false, Message = message, Succeeded = succeeded };
            if (fatal is not null) completion.TrySetException(fatal);
            else completion.TrySetResult();
        }
        Notify();
    }

    private static string CanceledMessage(bool removal) => removal
        ? "Removal canceled. Refresh model status; some files may have been removed."
        : "Download canceled. Refresh model status before trying again; existing files are retained.";

    private void Report(long generation, double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1) return;
        lock (_sync)
        {
            if (generation != _generation || _request is null || !_state.IsBusy || _state.IsClosing) return;
            _state = _state with { Progress = value };
        }
        Notify();
    }

    private void Notify()
    {
        foreach (Action listener in Changed?.GetInvocationList() ?? [])
            try { listener(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Model progress listener failed: " + ex.GetType().Name); }
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
