namespace TypeWhisper.Windows.Services;

/// <summary>
/// Defines the local api server contract.
/// </summary>
public interface ILocalApiServer
{
    /// <summary>
    /// Gets whether the local HTTP API listener is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the local HTTP API listener on the supplied TCP port.
    /// </summary>
    void Start(int port);

    /// <summary>
    /// Stops the local HTTP API listener if it is running.
    /// </summary>
    void Stop();
}
