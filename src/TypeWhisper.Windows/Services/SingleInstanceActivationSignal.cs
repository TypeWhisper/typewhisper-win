namespace TypeWhisper.Windows.Services;

/// <summary>
/// Signals an interactive launch to the already running TypeWhisper instance.
/// </summary>
internal sealed class SingleInstanceActivationSignal : IDisposable
{
    private const string DefaultEventName = "TypeWhisper-SingleInstance-Activation";
    private readonly EventWaitHandle _event;
    private bool _disposed;

    private SingleInstanceActivationSignal(EventWaitHandle activationEvent)
    {
        _event = activationEvent;
    }

    /// <summary>
    /// Opens the shared activation signal, creating it when necessary.
    /// </summary>
    public static SingleInstanceActivationSignal OpenOrCreate(string eventName = DefaultEventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return new SingleInstanceActivationSignal(
            new EventWaitHandle(false, EventResetMode.AutoReset, eventName));
    }

    /// <summary>
    /// Notifies the running instance that the user launched TypeWhisper again.
    /// </summary>
    public bool Notify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _event.Set();
    }

    /// <summary>
    /// Registers a callback that runs whenever another instance requests activation.
    /// </summary>
    public RegisteredWaitHandle Listen(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return ThreadPool.RegisterWaitForSingleObject(
            _event,
            static (state, timedOut) =>
            {
                if (!timedOut)
                    ((Action)state!).Invoke();
            },
            callback,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _event.Dispose();
    }
}
