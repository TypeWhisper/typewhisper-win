namespace TypeWhisper.Presentation;

/// <summary>Publishes one observable shutdown task before invoking potentially reentrant cleanup callbacks.</summary>
/// <remarks>Call Run on the owning UI thread; reentrant calls on that thread are supported.</remarks>
public sealed class AsyncShutdownCoordinator
{
    private Task? _completion;
    /// <summary>Starts cleanup once; repeated calls observe the same success or failure.</summary>
    public Task Run(Func<Task> shutdown)
    {
        if (_completion is not null) return _completion;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion.Task;
        _ = CompleteAsync(shutdown, completion);
        return _completion;
    }
    private static async Task CompleteAsync(Func<Task> shutdown, TaskCompletionSource completion)
    {
        try { await shutdown(); completion.TrySetResult(); }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
