namespace TypeWhisper.Core.Interfaces;

/// <summary>
/// Defines the active window service contract.
/// </summary>
public interface IActiveWindowService
{
    /// <summary>
    /// Returns active window handle.
    /// </summary>
    IntPtr GetActiveWindowHandle();
    /// <summary>
    /// Returns active window process name.
    /// </summary>
    string? GetActiveWindowProcessName();
    /// <summary>
    /// Returns active window title.
    /// </summary>
    string? GetActiveWindowTitle();
    /// <summary>
    /// Returns browser url.
    /// </summary>
    string? GetBrowserUrl();

    /// <summary>Returns distinct process names of all visible windows (sorted).</summary>
    IReadOnlyList<string> GetRunningAppProcessNames();
}
