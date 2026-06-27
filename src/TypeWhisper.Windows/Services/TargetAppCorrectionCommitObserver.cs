namespace TypeWhisper.Windows.Services;

/// <summary>
/// Stores explicit target-app correction commit signals.
/// </summary>
public sealed class TargetAppCorrectionCommitObserver : ITargetAppCorrectionCommitObserver
{
    private int _commitSignal;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TargetAppCorrectionCommitObserver class.
    /// </summary>
    public TargetAppCorrectionCommitObserver()
    {
    }

    /// <summary>
    /// Starts observing commit signals.
    /// </summary>
    public void Start()
    {
        Interlocked.Exchange(ref _commitSignal, 0);
    }

    /// <summary>
    /// Stops observing commit signals.
    /// </summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _commitSignal, 0);
    }

    /// <summary>
    /// Returns and clears whether a commit gesture has happened.
    /// </summary>
    public bool ConsumeCommitSignal()
        => Interlocked.Exchange(ref _commitSignal, 0) == 1;

    /// <summary>
    /// Raises a commit gesture for local automation runs.
    /// </summary>
    public void SignalCommitForAutomation()
    {
        Interlocked.Exchange(ref _commitSignal, 1);
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
