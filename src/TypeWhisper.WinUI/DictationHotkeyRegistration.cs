using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed class DictationHotkeyRegistration : IDisposable
{
    private readonly HotkeyRegistration _regular;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private bool _interrupted;
    private HashSet<string> _bindings = [];
    private HybridHotkeyState _state = new();
    private bool _disposed;
    internal string Value { get; private set; } = "";
    internal DictationHotkeyRegistration(Microsoft.UI.Xaml.Window window, Action<HybridHotkeyAction> invoke, Func<bool> isRecording,
        Func<RecordingMode>? recordingMode = null, Func<bool>? paused = null, int idBase = 0x6500)
    {
        recordingMode ??= () => RecordingMode.Hybrid;
        // Reserve ordinary chords, but use the hook for both press and release.
        _regular = new(window, () => { }, idBase);
        void Dispatch(HybridHotkeyAction? action)
        {
            // The hook runs on the installing UI thread. The receiver only captures
            // intent here and schedules audio work through its bounded coordinator.
            // This preserves event-time readiness before a long UI queue can drain.
            if (action is not null && !_disposed && !ShortcutRecorder.AnyEditing) invoke(action.Value);
        }
        _callback = (code, message, data) =>
        {
            if (code >= 0 && !_interrupted && !_disposed)
            {
                var key = Marshal.PtrToStructure<KeyData>(data);
                var acceptInjectedProbeInput = false;
#if DEBUG
                // Computer Use emits injected keys. Accept them only in the explicit
                // named-profile probe, whose session path returns before any audio work.
                acceptInjectedProbeInput = LocalDictationSession.WorkflowProbeEnabled || LocalDictationSession.CorrectionProbeEnabled;
#endif
                if ((key.Flags & 0x10) == 0 || acceptInjectedProbeInput)
                {
                    var down = message.ToInt64() is 0x100 or 0x104;
                    var up = message.ToInt64() is 0x101 or 0x105;
                    if (ShortcutRecorder.AnyEditing) _state = new();
                    else if (down || up)
                    {
                        var mode = recordingMode();
                        Dispatch(_state.Key((int)key.Key, down, Environment.TickCount64, _bindings, isRecording(), mode, paused?.Invoke() == true));
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _regular.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    internal void ObservePause() => _state.Suspend();

    internal void Interrupt()
    {
        _interrupted = true;
        _state.ResetAfterInterruption([]);
    }

    internal string? Recover()
    {
        if (_disposed) return null;
        var replacement = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
            return $"Could not restore dictation keyboard hook (Windows error {Marshal.GetLastWin32Error()}). Retry after unlocking, or restart TypeWhisper.";
        var previous = _hook;
        _hook = replacement;
        if (previous != IntPtr.Zero) UnhookWindowsHookEx(previous);
        // Generic modifier VKs alias the physical left/right keys. Seeding both
        // would leave a phantom generic key down after the physical key-up.
        _state.ResetAfterInterruption(Enumerable.Range(8, 247)
            .Where(key => key is not (0x10 or 0x11 or 0x12) && (GetAsyncKeyState(key) & 0x8000) != 0));
        _interrupted = false;
        return null;
    }

    internal string? TryChange(string value)
    {
        var chords = ShortcutRules.Split(value).Select(ShortcutRules.Normalize).Distinct().ToArray();
        foreach (var chord in chords)
            if (ShortcutRules.Validate(chord, true) is string error) return error;
        static bool ModifierOnly(string chord) => chord.Split('+').All(p => p is "CTRL" or "ALT" or "SHIFT" or "WIN");
        var registrationError = _regular.TryChange(string.Join(",", chords.Where(c => !ModifierOnly(c))));
        if (registrationError is not null) return registrationError;
        _bindings = chords.ToHashSet();
        _state = new();
        Value = string.Join(",", chords);
        return null;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; _regular.Dispose(); }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
