namespace TypeWhisper.Presentation;

/// <summary>Owns cancellation for one serialized operation and permanently rejects work after closing.</summary>
/// <remarks>Call mutations on the owning UI thread. Linked caller cancellation may originate elsewhere. The owner drains work before disposal; cancellation never implies completion.</remarks>
public sealed class OperationCancellationScope : IDisposable
{
    private CancellationTokenSource? _current;
    private bool _closed;
    /// <summary>The current operation token, or a non-cancelable token before the first operation.</summary>
    public CancellationToken Token => _current?.Token ?? CancellationToken.None;
    /// <summary>Begins the next serialized operation, optionally linked to caller cancellation.</summary>
    public CancellationToken Begin(CancellationToken caller = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _current?.Dispose();
        _current = CancellationTokenSource.CreateLinkedTokenSource(caller);
        return _current.Token;
    }
    /// <summary>Requests cancellation immediately, without waiting for the owner's operation gate.</summary>
    public void Cancel() => _current?.Cancel();
    /// <summary>Rejects future operations and requests cancellation of current work.</summary>
    public void Close() { _closed = true; Cancel(); }
    /// <summary>Releases token registrations after the owner has drained its operation.</summary>
    public void Dispose() { _closed = true; _current?.Dispose(); _current = null; }
}
