namespace TypeWhisper.Core.Interfaces;

public interface IActiveWindowService
{
    IntPtr GetActiveWindowHandle();
    string? GetActiveWindowProcessName();
    string? GetActiveWindowTitle();
    string? GetBrowserUrl();

    /// <summary>Returns distinct process names of all visible windows (sorted).</summary>
    IReadOnlyList<string> GetRunningAppProcessNames();
}
