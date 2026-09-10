namespace TypeWhisper.Presentation;

/// <summary>A user's recording intent, independent of native keyboard input.</summary>
public enum DictationInputAction
{
    /// <summary>Starts capture if the session is ready now.</summary>
    Start,
    /// <summary>Finishes capture, including a start still in progress.</summary>
    Stop,
    /// <summary>Discards capture, including a speculative start still in progress.</summary>
    Cancel,
    /// <summary>Starts or stops according to current capture intent.</summary>
    Toggle
}

/// <summary>Coordinates UI-thread recording intent with one operation and at most one pending terminal action. Starts are never queued behind final processing.</summary>
public sealed class DictationInputCoordinator : IDisposable
{
    private readonly Func<Task> _start, _stop, _cancel;
    private readonly Func<bool> _recording, _canStart;
    private readonly Func<RecordingMode> _mode;
    private readonly Func<Action, bool> _dispatch;
    private readonly Action<Exception>? _reportError;
    private bool _busy, _starting, _disposed;
    private DictationInputAction? _terminal;
    private RecordingMode _observedMode;
    private TaskCompletionSource _completion = Completed();

    /// <summary>Creates a coordinator. Submit, configuration observations and disposal must use the same UI thread; dispatch schedules audio work on that thread.</summary>
    public DictationInputCoordinator(Func<Task> start, Func<Task> stop, Func<Task> cancel,
        Func<bool> recording, Func<bool> canStart, Func<RecordingMode> mode,
        Func<Action, bool>? dispatch = null, Action<Exception>? reportError = null)
    {
        _start = start; _stop = stop; _cancel = cancel; _recording = recording; _canStart = canStart; _mode = mode;
        _dispatch = dispatch ?? (action => { action(); return true; }); _reportError = reportError;
        _observedMode = mode();
    }

    /// <summary>Includes a reserved or asynchronous start so a second press is interpreted as a stop.</summary>
    public bool IsRecordingOrStarting => _starting || _recording();
    /// <summary>Completes after the accepted operation and its pending stop or cancel have finished.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Discards an unfinished start across lock/sleep without stopping an already established recording.</summary>
    public void InterruptPendingGesture()
    {
        if (_starting) _terminal = DictationInputAction.Cancel;
    }

    /// <summary>Captures intent immediately, before UI dispatch. A cancel supersedes a pending stop; competing starts are discarded.</summary>
    public Task SubmitAsync(DictationInputAction action, Func<Task>? startOverride = null)
    {
        if (_disposed) return Completion;
        ObserveMode();
        if (action == DictationInputAction.Toggle) action = IsRecordingOrStarting ? DictationInputAction.Stop : DictationInputAction.Start;
        if (_starting)
        {
            if (action == DictationInputAction.Cancel || action == DictationInputAction.Stop && _terminal != DictationInputAction.Cancel)
                _terminal = action;
            return Completion;
        }
        if (_busy) return Completion;
        if (action == DictationInputAction.Start)
        {
            if (_recording() || !_canStart()) return Completion;
            _starting = true;
        }
        else if (!_recording()) return Completion;
        _busy = true; _terminal = null;
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _completion;
        if (!_dispatch(() => _ = RunAsync(action, completion, startOverride))) Finish(completion);
        return completion.Task;
    }

    /// <summary>Invalidates a pending start when the recording mode changes, even if it later changes back.</summary>
    public void ObserveMode()
    {
        var mode = _mode();
        if (mode != _observedMode && _starting) _terminal = DictationInputAction.Cancel;
        _observedMode = mode;
    }

    private async Task RunAsync(DictationInputAction action, TaskCompletionSource completion, Func<Task>? startOverride)
    {
        try
        {
            if (action == DictationInputAction.Start)
            {
                ObserveMode();
                if (_disposed || _terminal == DictationInputAction.Cancel || !_canStart()) return;
                await (startOverride ?? _start)();
                ObserveMode();
                _starting = false;
                var terminal = _disposed ? DictationInputAction.Cancel : _terminal;
                if (_recording() && terminal is { } requested)
                    await (requested == DictationInputAction.Cancel ? _cancel() : _stop());
            }
            else
                await (action == DictationInputAction.Cancel || _disposed ? _cancel() : _stop());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave a partially started capture orphaned after a failed start.
            if (_starting && _recording())
                try { await _cancel(); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { _reportError?.Invoke(cleanup); }
            _reportError?.Invoke(ex);
        }
        finally { Finish(completion); }
    }

    private void Finish(TaskCompletionSource completion)
    {
        _starting = false; _busy = false; _terminal = null;
        completion.TrySetResult();
    }

    /// <summary>Rejects new input and discards pending capture; an in-flight start is canceled as soon as it completes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        if (_starting) _terminal = DictationInputAction.Cancel;
        else if (!_busy && _recording()) _ = SubmitAsync(DictationInputAction.Cancel);
        _disposed = true;
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult(); return result;
    }
}
