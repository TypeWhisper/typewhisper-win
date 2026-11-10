using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI;

/// <summary>A top-level window as Windows lists it, front to back.</summary>
internal readonly record struct TopLevelWindow(IntPtr Window, uint ProcessId, bool Visible, bool Owned, bool ToolWindow, bool Cloaked,
    int TitleLength, bool Caption = false, int Width = 0, int Height = 0);

// Finds the window of the app a workflow sends its dictation to (#764), such as a chat app next to a game.
internal static class TargetAppWindow
{
    // Smaller hidden windows are helpers, such as crash reporters, not the app's main window.
    private const int MinimumHiddenSize = 200;

    /// <summary>
    /// The front-most window the user can switch to in one of the app's processes: visible or minimized, not owned by
    /// another window, not a tool window and titled; one on the current virtual desktop first, then one on another.
    /// Without one, an app closed to the notification area offers its hidden main window: titled, with a title bar and
    /// of a normal size, which leaves out the hidden helper windows such apps keep. The result is hidden then.
    /// </summary>
    internal static TopLevelWindow? Choose(IEnumerable<TopLevelWindow> frontToBack, IReadOnlySet<uint> processIds)
    {
        var candidates = frontToBack.Where(item => processIds.Contains(item.ProcessId) && !item.Owned && !item.ToolWindow
            && item.TitleLength > 0).ToArray();
        return candidates.Cast<TopLevelWindow?>().FirstOrDefault(item => item!.Value is { Visible: true, Cloaked: false })
            // A window on another virtual desktop is open too: it is brought to the front rather than the app started
            // a second time.
            ?? candidates.Cast<TopLevelWindow?>().FirstOrDefault(item => item!.Value is { Visible: true, Cloaked: true })
            ?? candidates.Cast<TopLevelWindow?>().FirstOrDefault(item => item!.Value is
                { Visible: false, Cloaked: false, Caption: true, Width: >= MinimumHiddenSize, Height: >= MinimumHiddenSize });
    }

    /// <summary>The app's window to insert into, or null when none of its processes has one.</summary>
    internal static TopLevelWindow? Find(string processName)
    {
        var processIds = new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName(processName))
            using (process)
            {
                if (process.Id != Environment.ProcessId) processIds.Add((uint)process.Id);
            }
        if (processIds.Count == 0) return null;
        var windows = new List<TopLevelWindow>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processIds.Contains(processId))
            {
                GetWindowRect(window, out var bounds);
                windows.Add(new(window, processId, IsWindowVisible(window), GetWindow(window, Owner) != IntPtr.Zero,
                    (GetWindowLongPtr(window, ExtendedStyle).ToInt64() & ToolWindowStyle) != 0, IsCloaked(window), GetWindowTextLength(window),
                    (GetWindowLongPtr(window, Style).ToInt64() & CaptionStyle) == CaptionStyle, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top));
            }
            return true;
        }, IntPtr.Zero);
        return Choose(windows, processIds);
    }

    /// <summary>Hides a window again, as the app had it in the notification area.</summary>
    internal static void Hide(IntPtr window) => ShowWindowAsync(window, HideWindow);

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, CloakedAttribute, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const uint Owner = 4;
    private const int Style = -16;
    private const int ExtendedStyle = -20;
    private const long CaptionStyle = 0x00C00000;
    private const long ToolWindowStyle = 0x80;
    private const int CloakedAttribute = 14;
    private const int HideWindow = 0;
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Bounds bounds);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
