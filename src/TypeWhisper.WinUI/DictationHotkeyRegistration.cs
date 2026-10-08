using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class DictationHotkeyRegistration : IShortcutRegistrationBackend, IDisposable
{
    private readonly HotkeyRegistration _regular;
    private readonly NativeMethods.HookProc _callback;
    private IntPtr _hook;
    private bool _interrupted;
    private HashSet<string> _bindings = [];
    private HybridHotkeyState _state = new();
    private bool _disposed;
    public string Value { get; private set; } = "";
    // Lets the shortcut recorder tell AltGr from a real Ctrl, which XAML key events cannot.
    internal static bool AltGrControlDown { get; private set; }
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
                var key = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(data);
                var altGr = ShortcutKeys.IsAltGrControl(key.Key, key.Scan);
                var pressed = message.ToInt64() is 0x100 or 0x104;
                // A real left Ctrl or the right Alt release also ends AltGr, in case its Ctrl release was missed.
                if (key.Key == 0xA2 || key.Key == 0xA5 && !pressed) AltGrControlDown = altGr && pressed;
                // Keys injected by other tools count like physical ones; only our own paste, copy and media keys are skipped.
                if (!OwnKeyboardInput.Sent(key.Flags, key.Extra))
                {
                    var down = message.ToInt64() is 0x100 or 0x104;
                    var up = message.ToInt64() is 0x101 or 0x105;
                    if (ShortcutRecorder.AnyEditing) _state = new();
                    else if (down || up)
                    {
                        var mode = recordingMode();
                        Dispatch(_state.Key(altGr ? HybridHotkeyState.AltGrControl : (int)key.Key, down, Environment.TickCount64, _bindings, isRecording(), mode, paused?.Invoke() == true,
                            held => (NativeMethods.GetAsyncKeyState(held == HybridHotkeyState.AltGrControl ? 0xA2 : held) & 0x8000) != 0));
                    }
                }
            }
            return NativeMethods.CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _regular.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    internal void ObservePause() => _state.Suspend();

    internal void Interrupt()
    {
        _interrupted = true;
        AltGrControlDown = false;
        _state.ResetAfterInterruption([]);
    }

    internal string? Recover()
    {
        if (_disposed) return null;
        var replacement = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (replacement == IntPtr.Zero)
            return Loc.T("Could not restore dictation keyboard hook (Windows error {0}). Retry after unlocking, or restart TypeWhisper.", Marshal.GetLastWin32Error());
        var previous = _hook;
        _hook = replacement;
        if (previous != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(previous);
        // Generic modifier VKs alias the physical left/right keys. Seeding both
        // would leave a phantom generic key down after the physical key-up.
        _state.ResetAfterInterruption(Enumerable.Range(8, 247)
            .Where(key => key is not (0x10 or 0x11 or 0x12) && (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0));
        _interrupted = false;
        return null;
    }

    public string? TryChange(string value)
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
    public void Dispose() { if (_disposed) return; _disposed = true; NativeMethods.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; _regular.Dispose(); }
}
