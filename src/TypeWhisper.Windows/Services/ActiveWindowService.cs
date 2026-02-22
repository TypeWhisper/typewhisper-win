using System.Diagnostics;
using System.Windows.Automation;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

public sealed class ActiveWindowService : IActiveWindowService
{
    private static readonly int OwnProcessId = Environment.ProcessId;

    private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "vivaldi", "chromium", "firefox", "waterfox"
    };

    // Cache: avoid expensive UIA calls when nothing changed
    private IntPtr _lastHwnd;
    private string? _lastTitle;
    private string? _cachedUrl;

    public IReadOnlyList<string> GetRunningAppProcessNames()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p =>
                {
                    try { return p.MainWindowHandle != IntPtr.Zero && p.Id != OwnProcessId; }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try { return p.ProcessName; }
                    catch { return null; }
                })
                .Where(n => n is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch
        {
            return [];
        }
    }

    public string? GetActiveWindowProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == OwnProcessId) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public string? GetActiveWindowTitle()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        var length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0) return null;

        var buffer = new char[length + 1];
        var result = NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Length);
        return result > 0 ? new string(buffer, 0, result) : null;
    }

    public string? GetBrowserUrl()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;

        // Only check for known browser processes
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == OwnProcessId) return null;

        string? processName;
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            processName = proc.ProcessName;
        }
        catch { return null; }

        if (processName is null || !BrowserProcessNames.Contains(processName))
            return null;

        // Cache: same window + same title = same URL (tab hasn't changed)
        var title = GetActiveWindowTitle();
        if (hwnd == _lastHwnd && title == _lastTitle && _cachedUrl is not null)
            return _cachedUrl;

        _lastHwnd = hwnd;
        _lastTitle = title;
        _cachedUrl = null;

        try
        {
            var window = AutomationElement.FromHandle(hwnd);

            // Strategy 1: Search toolbar children for Edit controls (fast, avoids web content tree)
            var url = FindUrlInToolbars(window);

            // Strategy 2: Fallback — search top-level pane children
            url ??= FindUrlInPanes(window);

            _cachedUrl = url;
            return url;
        }
        catch
        {
            // UIA can throw if the target process is unresponsive
            return null;
        }
    }

    /// <summary>
    /// Search ToolBar elements (direct children of window) for an Edit control with a URL value.
    /// This is the primary strategy for Chromium browsers.
    /// </summary>
    private static string? FindUrlInToolbars(AutomationElement window)
    {
        var toolbarCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.ToolBar);
        var toolbars = window.FindAll(TreeScope.Children, toolbarCondition);

        foreach (AutomationElement toolbar in toolbars)
        {
            var url = FindEditWithUrl(toolbar);
            if (url is not null) return url;
        }

        return null;
    }

    /// <summary>
    /// Fallback: search Pane children of window for Edit controls.
    /// Some browsers nest the address bar inside a Pane, not a ToolBar.
    /// </summary>
    private static string? FindUrlInPanes(AutomationElement window)
    {
        var paneCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.Pane);
        var panes = window.FindAll(TreeScope.Children, paneCondition);

        foreach (AutomationElement pane in panes)
        {
            var url = FindEditWithUrl(pane);
            if (url is not null) return url;
        }

        return null;
    }

    /// <summary>
    /// Find the first Edit control within the given element whose value looks like a URL.
    /// </summary>
    private static string? FindEditWithUrl(AutomationElement parent)
    {
        var editCondition = new PropertyCondition(
            AutomationElement.ControlTypeProperty, ControlType.Edit);

        var edits = parent.FindAll(TreeScope.Descendants, editCondition);
        foreach (AutomationElement edit in edits)
        {
            try
            {
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern vp)
                {
                    var value = vp.Current.Value?.Trim();
                    if (!string.IsNullOrEmpty(value) && IsLikelyUrl(value))
                        return NormalizeUrl(value);
                }
            }
            catch
            {
                // Individual element access can fail
            }
        }

        return null;
    }

    /// <summary>
    /// Check if a string looks like a browser URL.
    /// Chromium browsers may hide the protocol (showing "x.com" instead of "https://x.com").
    /// </summary>
    private static bool IsLikelyUrl(string value)
    {
        if (value.Length < 3 || value.Length > 2048) return false;

        // Explicit protocols
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return true;

        // Chrome hides protocol: "x.com", "github.com/user/repo"
        // Must contain a dot and no spaces
        if (value.Contains(' ') || !value.Contains('.')) return false;

        // Check for common TLD patterns
        var host = value.Split('/')[0];
        return host.Contains('.');
    }

    private static string NormalizeUrl(string value)
    {
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return value;

        return "https://" + value;
    }
}
