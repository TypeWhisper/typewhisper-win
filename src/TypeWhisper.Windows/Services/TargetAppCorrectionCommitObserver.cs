using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Observes explicit target-app correction commit signals.
/// </summary>
public sealed class TargetAppCorrectionCommitObserver : ITargetAppCorrectionCommitObserver
{
    private readonly object _hookLock = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hookId;
    private int _commitSignal;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the TargetAppCorrectionCommitObserver class.
    /// </summary>
    public TargetAppCorrectionCommitObserver()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Starts observing commit signals.
    /// </summary>
    public void Start()
    {
        Interlocked.Exchange(ref _commitSignal, 0);
        StartHook();
    }

    /// <summary>
    /// Stops observing commit signals.
    /// </summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _commitSignal, 0);
        StopHook();
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

    internal static bool ShouldSignalCommitKey(
        int nCode,
        IntPtr wParam,
        in NativeMethods.KBDLLHOOKSTRUCT hookStruct)
    {
        if (nCode < 0 ||
            (wParam != NativeMethods.WM_KEYDOWN && wParam != NativeMethods.WM_SYSKEYDOWN) ||
            (hookStruct.flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            return false;
        }

        return hookStruct.vkCode == (uint)NativeMethods.VK_RETURN ||
            hookStruct.vkCode == (uint)NativeMethods.VK_TAB;
    }

    private void StartHook()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(StartHookOnCurrentThread);
            return;
        }

        StartHookOnCurrentThread();
    }

    private void StartHookOnCurrentThread()
    {
        lock (_hookLock)
        {
            if (_hookId != IntPtr.Zero)
                return;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            if (module is null)
                return;

            _hookId = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandleW(module.ModuleName),
                0);
        }
    }

    private void StopHook()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(StopHookOnCurrentThread);
            return;
        }

        StopHookOnCurrentThread();
    }

    private void StopHookOnCurrentThread()
    {
        lock (_hookLock)
        {
            if (_hookId == IntPtr.Zero)
                return;

            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (ShouldSignalCommitKey(nCode, wParam, hookStruct))
                Interlocked.Exchange(ref _commitSignal, 1);
        }
        catch (ArgumentException ex)
        {
            Debug.WriteLine($"Target-app correction commit hook failed: {ex.Message}");
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
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
