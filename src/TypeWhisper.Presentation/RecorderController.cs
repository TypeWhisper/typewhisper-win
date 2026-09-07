namespace TypeWhisper.Presentation;

/// <summary>State of a real recorder capture and its local file publication.</summary>
public enum RecorderState
{
    /// <summary>No capture is active.</summary>
    Ready,
    /// <summary>Audio sources are capturing.</summary>
    Recording,
    /// <summary>Sources are stopping or audio is being saved.</summary>
    Saving,
    /// <summary>Captured audio remains in memory for another save attempt.</summary>
    SaveFailed,
    /// <summary>The WAV file was published successfully.</summary>
    Saved
}

/// <summary>Serializes capture, retryable saving and shutdown on its owning UI thread.</summary>
public sealed class RecorderController(Func<IDisposable> reserve, Func<bool, bool, Task> start,
    Func<Task<float[]>> stop, Func<float[], Task<string>> save)
{
    private IDisposable? _reservation;
    private float[]? _unsaved;
    private Task _pending = Task.CompletedTask;
    private bool _closing;
    private bool _emptyCapture;
    /// <summary>The current recorder state.</summary>
    public RecorderState State { get; private set; }
    /// <summary>Whether an asynchronous transition is pending.</summary>
    public bool Busy { get; private set; }
    /// <summary>The published file, if saving succeeded.</summary>
    public string? FilePath { get; private set; }
    /// <summary>The duration of the original captured samples.</summary>
    public TimeSpan Duration { get; private set; }
    /// <summary>A capture or save failure, retained until the next successful action.</summary>
    public string? Error { get; private set; }
    /// <summary>Raised on the owner thread when state changes.</summary>
    public event Action? Changed;
    /// <summary>Clears the saved result after its file was deleted, without affecting an active capture or save.</summary>
    public bool ForgetDeletedFile(string path)
    {
        if (Busy || State != RecorderState.Saved || !string.Equals(FilePath, path,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        FilePath = null;
        Duration = TimeSpan.Zero;
        State = RecorderState.Ready;
        Error = null;
        NotifyChanged();
        return true;
    }
    /// <summary>Stops and saves at the recorder's sixty-minute limit.</summary>
    public Task StopAtLimitAsync(TimeSpan elapsed) => State == RecorderState.Recording && elapsed >= TimeSpan.FromHours(1)
        ? StopAndSaveAsync() : Task.CompletedTask;

    /// <summary>Reserves capture exclusively and starts the selected sources.</summary>
    public Task StartAsync(bool microphone, bool systemAudio) => Run(async () =>
    {
        if (_closing || State is RecorderState.Recording or RecorderState.SaveFailed) throw new InvalidOperationException("Finish or save the current recording first.");
        if (!microphone && !systemAudio) throw new InvalidOperationException("Select at least one audio source.");
        _reservation = reserve();
        _emptyCapture = false;
        try { await start(microphone, systemAudio); State = RecorderState.Recording; FilePath = null; Duration = TimeSpan.Zero; }
        catch (RecorderCleanupException) { State = RecorderState.Recording; throw; }
        catch { _reservation.Dispose(); _reservation = null; throw; }
    });

    /// <summary>Stops capture and publishes a WAV; failed writes keep all samples available for retry.</summary>
    public Task StopAndSaveAsync() => Run(async () =>
    {
        if (State != RecorderState.Recording) return;
        State = RecorderState.Saving; NotifyChanged();
        try
        {
            try { _unsaved = await stop(); }
            catch (RecorderCleanupException) { State = RecorderState.Recording; throw; }
            catch { State = RecorderState.Ready; throw; }
            if (_unsaved.Length == 0) { _emptyCapture = true; State = RecorderState.Ready; throw new InvalidOperationException("No usable audio was captured."); }
            Duration = TimeSpan.FromSeconds(_unsaved.Length / 16000.0);
            await SaveAsync();
        }
        finally { if (State != RecorderState.Recording) { _reservation?.Dispose(); _reservation = null; } }
    });

    /// <summary>Retries publication without recording or decoding again.</summary>
    public Task RetrySaveAsync() => Run(SaveAsync);

    private async Task SaveAsync()
    {
        if (_unsaved is null || _unsaved.Length == 0) throw new InvalidOperationException("No usable audio was captured.");
        State = RecorderState.Saving; NotifyChanged();
        try { FilePath = await save(_unsaved); _unsaved = null; State = RecorderState.Saved; }
        catch { State = RecorderState.SaveFailed; throw; }
    }

    /// <summary>Drains transitions and saves an active recording before the session releases its resources.</summary>
    public async Task ShutdownAsync()
    {
        _closing = true;
        try { await _pending; } catch { /* Inspect retained state and retry below. */ }
        if (State == RecorderState.Recording)
        {
            try { await StopAndSaveAsync(); }
            catch when (_emptyCapture && State == RecorderState.Ready && _reservation is null)
            { /* Sources stopped cleanly and there is no audio to save. */ }
        }
        else if (State == RecorderState.SaveFailed) await RetrySaveAsync();
    }

    private Task Run(Func<Task> action)
    {
        if (Busy) return _pending;
        Busy = true; Error = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion.Task;
        _ = ExecuteAsync(action, completion);
        return completion.Task;
    }
    private async Task ExecuteAsync(Func<Task> action, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { NotifyChanged(); await action(); }
        catch (Exception ex) { Error = ex.Message; failure = ex; }
        finally
        {
            Busy = false;
            NotifyChanged();
        }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    private void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action observer in changed.GetInvocationList())
            try { observer(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { System.Diagnostics.Trace.TraceError("Recorder state observer failed: {0}", ex); }
    }
}
