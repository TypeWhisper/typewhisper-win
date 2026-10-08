namespace TypeWhisper.WinUI;

// UI Automation objects must be created and used outside the owning window's STA.
// The thread pool uses the process MTA; the gate also prevents disposal during a provider call.
internal sealed class SerializedBackgroundWork
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(operation, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
