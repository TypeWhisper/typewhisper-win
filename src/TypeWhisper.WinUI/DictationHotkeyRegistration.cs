using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI;

internal sealed class DictationHotkeyRegistration : IDisposable
{
    private readonly PrototypeHotkeyRegistration _regular;
    private readonly HookProc _callback;
    private readonly IntPtr _hook;
    private HashSet<string> _bindings = [];
    private HybridHotkeyState _state = new();
    private bool _disposed;
    internal string Value { get; private set; } = "";
    internal DictationHotkeyRegistration(Microsoft.UI.Xaml.Window window, Action<HybridHotkeyAction> invoke, Func<bool> isRecording)
    {
        // Reserve ordinary chords, but use the hook for both press and release.
        _regular = new(window, () => { }, 0x6500);
        void Dispatch(HybridHotkeyAction? action)
        {
            if (action is not null)
                window.DispatcherQueue.TryEnqueue(() => { if (!_disposed && !PrototypeShortcutRecorder.AnyEditing) invoke(action.Value); });
        }
        _callback = (code, message, data) =>
        {
            if (code >= 0)
            {
                var key = Marshal.PtrToStructure<KeyData>(data);
                if ((key.Flags & 0x10) == 0)
                {
                    var down = message.ToInt64() is 0x100 or 0x104;
                    var up = message.ToInt64() is 0x101 or 0x105;
                    if (PrototypeShortcutRecorder.AnyEditing) _state = new();
                    else if (down || up) Dispatch(_state.Key((int)key.Key, down, Environment.TickCount64, _bindings, isRecording()));
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { _regular.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    internal string? TryChange(string value)
    {
        var chords = PrototypeShortcutRules.Split(value).Select(PrototypeShortcutRules.Normalize).Distinct().ToArray();
        foreach (var chord in chords)
            if (PrototypeShortcutRules.Validate(chord, true) is string error) return error;
        static bool ModifierOnly(string chord) => chord.Split('+').All(p => p is "CTRL" or "ALT" or "SHIFT" or "WIN");
        var registrationError = _regular.TryChange(string.Join(",", chords.Where(c => !ModifierOnly(c))));
        if (registrationError is not null) return registrationError;
        _bindings = chords.ToHashSet();
        _state = new();
        Value = string.Join(",", chords);
        return null;
    }
    public void Dispose() { _disposed = true; UnhookWindowsHookEx(_hook); _regular.Dispose(); }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
