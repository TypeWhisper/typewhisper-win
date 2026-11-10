using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI.Platform;

// Brings another app's window to the front from the background, where Windows usually refuses SetForegroundWindow.
internal static class WindowActivation
{
    private const int Restore = 9;

    /// <summary>
    /// Restores a minimized window and brings it to the front. Returns once the window is in front, or false when it
    /// closed, belongs to another process by now, or the deadline passed. It never joins input queues: Windows can hold
    /// that call for as long as an app that is still starting up does not answer, which froze deliveries for seconds.
    /// </summary>
    internal static async Task<bool> ActivateAsync(IntPtr window, uint process, string stage, CancellationToken cancellation,
        TimeSpan? timeout = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(2);
        bool Valid() => NativeMethods.IsWindow(window) && NativeMethods.GetWindowThreadProcessId(window, out var owner) != 0 && owner == process;
        bool Current() => NativeMethods.GetForegroundWindow() == window;
        cancellation.ThrowIfCancellationRequested();
        if (!Valid()) return false;
        if (Current()) return true;
        if (NativeMethods.IsIconic(window)) ShowWindowAsync(window, Restore);
        for (var attempt = 1; ; attempt++)
        {
            // Windows lets the process that sent the last input take the foreground. A mouse move by zero pixels makes
            // that TypeWhisper without changing anything on screen or reaching the app as a key.
            GrantForeground();
            var requested = NativeMethods.SetForegroundWindow(window);
            if (attempt == 1 || Current()) AppDiagnostics.Write($"{stage}.request attempt={attempt} accepted={requested} current={Current()}");
            // An accepted request completes asynchronously, usually within a few milliseconds; a window that is just
            // being shown can take longer, so the request is repeated until the deadline.
            for (var waited = 0; waited < 250; waited += 25)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Valid()) return false;
                if (Current()) return true;
                if (clock.Elapsed >= limit) { AppDiagnostics.Write($"{stage}.request-expired attempt={attempt}"); return false; }
                await Task.Delay(25, cancellation);
            }
        }
    }

    /// <summary>
    /// Requests the foreground for <paramref name="window"/> while TypeWhisper's thread shares the input state of the
    /// window in front and of the target, which Windows requires before it lets a background process activate a window.
    /// The queues are joined and released on the calling thread. No keys are sent.
    /// </summary>
    internal static void ActivateWithInputThread(IntPtr window, uint process, Func<bool> expired, string stage)
    {
        var currentThread = GetCurrentThreadId();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(window, out var owner);
        if (owner != process || targetThread == 0) return;
        var foregroundAttached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        var targetAttached = false;
        try
        {
            // Activation and focus belong to the target's input queue too. Connecting
            // only to the window being left does not activate the original queue.
            targetAttached = targetThread != currentThread && targetThread != foregroundThread &&
                AttachThreadInput(currentThread, targetThread, true);
            AppDiagnostics.Write($"{stage}.queues foreground={foregroundAttached} target={targetAttached}");
            if (expired()) return;
            var requested = NativeMethods.SetForegroundWindow(window);
            if (!expired() && (targetAttached || targetThread == currentThread || (targetThread == foregroundThread && foregroundAttached)))
                SetActiveWindow(window);
            AppDiagnostics.Write($"{stage}.activation accepted={requested} current={NativeMethods.GetForegroundWindow() == window}");
        }
        finally
        {
            if (targetAttached) AttachThreadInput(currentThread, targetThread, false);
            if (foregroundAttached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>Sends a mouse move by zero pixels, so TypeWhisper counts as the process that sent the last input.</summary>
    internal static void GrantForeground()
    {
        NativeMethods.Input[] inputs = [new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0001, Extra = OwnKeyboardInput.Marker } } }];
        NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint source, uint target, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
}
