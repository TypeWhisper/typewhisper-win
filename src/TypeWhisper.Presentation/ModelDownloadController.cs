namespace TypeWhisper.Presentation;

/// <summary>An immutable state for one explicit download; progress is supplied only by the plugin.</summary>
public sealed record ModelDownloadState(bool IsBusy, bool IsClosing, double? Progress, string? Message, bool Succeeded);

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
        CancellationTokenSource request;
        TaskCompletionSource completion;
        long generation;
        lock (_sync)
        {
            if (_state.IsClosing || !_completion.IsCompleted)
                throw new InvalidOperationException("Finish the current model operation before starting another.");
            request = _request = new();
            generation = ++_generation;
            _state = new(true, false, null, "Downloading model…", false);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion.Task;
        }
        _ = RunCoreAsync(download, request, generation, completion);
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
                _state = _state with { Message = "Canceling model download…" };
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
        CancellationTokenSource request, long generation, TaskCompletionSource completion)
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
            message = "Model downloaded. Select it explicitly to use it.";
        }
        catch (OperationCanceledException)
        { message = "Download canceled. Refresh model status before trying again; existing files are retained."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { message = "The model could not be downloaded. Check its requirements and configuration before trying again."; }
        catch (Exception ex)
        { fatal = ex; message = "The model download could not finish."; }
        lock (_sync)
            if (succeeded && generation == _generation && !request.IsCancellationRequested)
                _state = _state with { Message = "Finishing model download…" };
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
                message = "Download canceled. Refresh model status before trying again; existing files are retained.";
            }
            request.Dispose();
            _callbacks = Task.CompletedTask;
            _state = _state with { IsBusy = false, Message = message, Succeeded = succeeded };
            if (fatal is not null) completion.TrySetException(fatal);
            else completion.TrySetResult();
        }
        Notify();
    }

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
