namespace TypeWhisper.WinUI;

// One module handler for a host event. Host events arrive on any thread; the handler runs on the UI thread through
// post and stops with Dispose, also for notifications already queued. A handler that throws is reported, because an
// exception escaping a UI thread callback would end the app.
internal sealed class ModuleObservation : IDisposable
{
    private readonly Action<Action> _post;
    private readonly Action<string, Exception> _report;
    private readonly string _stage;
    private Action? _unsubscribe;
    private int _disposed;

    internal ModuleObservation(Action<Action> post, Action<string, Exception> report, string stage)
    {
        _post = post;
        _report = report;
        _stage = stage;
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // Set once the host event is subscribed; runs on the first Dispose.
    internal Action? Unsubscribe
    {
        set
        {
            _unsubscribe = value;
            if (IsDisposed) Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }

    internal void Deliver(Action notify) => _post(() =>
    {
        if (IsDisposed) return;
        try { notify(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _report(_stage, ex); }
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
