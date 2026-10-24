using System.ComponentModel;
using System.Runtime.InteropServices;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed class HotkeyRegistration : IShortcutRegistrationBackend, IMouseShortcutHandler, IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private readonly nuint SubclassId;

    private readonly IntPtr _hwnd;
    private readonly RegisteredShortcutDispatch _dispatch;
    private readonly NativeMethods.SubclassProc _subclassProc;
    private bool _registered;
    private readonly Dictionary<string, int> _bindings = new();
    private int _nextId;
    // RegisterHotKey cannot reserve mouse buttons; these chords go through the shared mouse hook instead.
    private const int MouseBinding = 0;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    // The mouse hook thread reads these; the UI thread replaces them under the lock.
    private readonly object _mouseLock = new();
    private readonly MouseShortcutPresses _mousePresses = new();
    private HashSet<string> _mouseChords = [];
    private bool _mouseHooked;

    public string Value => string.Join(",", _bindings.Keys);

    internal HotkeyRegistration(Microsoft.UI.Xaml.Window window, Action callback, int idBase)
        : this(window, (_, stale) => { if (!stale) callback(); }, idBase) { }

    internal HotkeyRegistration(Microsoft.UI.Xaml.Window window, Action<string, bool> callback, int idBase)
    {
        _nextId = idBase;
        SubclassId = (nuint)idBase;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _dispatch = new(callback);
        _subclassProc = WindowSubclassProc;
        _queue = window.DispatcherQueue;

        if (!NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, SubclassId, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to receive global shortcuts for this window.");

        _registered = true;
    }

    public string? TryChange(string value)
    {
        var requested = ShortcutRules.Split(value).Select(ShortcutRules.Normalize).Distinct().ToArray();
        var added = new Dictionary<string, int>();
        foreach (var chord in requested.Where(chord => !_bindings.ContainsKey(chord)))
        {
            var error = ShortcutRules.Validate(chord, false);
            var parts = chord.Split('+');
            if (error is not null || !ShortcutKeys.TryParse(parts[^1], out var vk) || vk == 0x7B)
            {
                Unregister(added.Values);
                return error ?? Loc.T("Choose another key with modifiers. F12 is reserved.");
            }
            if (ShortcutKeys.IsMouseButton(vk)) { added.Add(chord, MouseBinding); continue; }
            uint mods = ModNoRepeat;
            foreach (var modifier in parts[..^1]) mods |= modifier switch { "ALT" => ModAlt, "CTRL" => ModControl, "SHIFT" => 4u, "WIN" => 8u, _ => 0u };
            var newId = ++_nextId;
            if (!RegisterHotKey(_hwnd, newId, mods, (uint)vk))
            {
                Unregister(added.Values);
                return Loc.T("{0} is unavailable or already used by another app. Your previous shortcuts are unchanged.", chord);
            }
            added.Add(chord, newId);
        }
        var mouseChords = requested.Where(ShortcutKeys.IsMouseChord).ToHashSet();
        if (mouseChords.Count > 0 && !_mouseHooked)
        {
            if (MouseShortcutHook.Add(this) is { } hookError) { Unregister(added.Values); return hookError; }
            _mouseHooked = true;
        }
        foreach (var old in _bindings.Keys.Where(key => !requested.Contains(key)).ToArray())
        {
            Unregister([_bindings[old]]);
            _bindings.Remove(old);
        }
        foreach (var pair in added) _bindings.Add(pair.Key, pair.Value);
        lock (_mouseLock) _mouseChords = mouseChords;
        if (mouseChords.Count == 0) StopMouse();
        return null;
    }

    private void Unregister(IEnumerable<int> ids)
    {
        foreach (var id in ids.Where(id => id != MouseBinding)) UnregisterHotKey(_hwnd, id);
    }

    private void StopMouse()
    {
        if (!_mouseHooked) return;
        MouseShortcutHook.Remove(this);
        _mouseHooked = false;
        ((IMouseShortcutHandler)this).Reset();
    }

    // Runs on the mouse hook thread. The press is taken there, the action on the UI thread. A press
    // that waits too long there counts as stale, like a late WM_HOTKEY.
    bool IMouseShortcutHandler.Button(int key, bool down, uint time, string modifiers)
    {
        bool owned;
        string? chord;
        lock (_mouseLock)
        {
            if (!_registered) return false;
            owned = _mousePresses.Button(key, down, modifiers, _mouseChords, true, out chord);
        }
        if (chord is { } pressed)
            _queue.TryEnqueue(() =>
            {
                if (_registered && _mouseChords.Contains(pressed))
                    _dispatch.Invoke(pressed, time, Environment.TickCount64, ShortcutRecorder.CaptureRegisteredShortcut);
            });
        return owned;
    }

    void IMouseShortcutHandler.Reset()
    {
        lock (_mouseLock) _mousePresses.Reset();
    }

    private IntPtr WindowSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        IntPtr referenceData)
    {
        if (message == WmHotkey && _bindings.FirstOrDefault(binding => binding.Value == wParam.ToInt32()).Key is { } chord)
        {
            _dispatch.Invoke(chord, unchecked((uint)NativeMethods.GetMessageTime()), Environment.TickCount64,
                ShortcutRecorder.CaptureRegisteredShortcut);
        }

        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        lock (_mouseLock) _registered = false;
        Unregister(_bindings.Values);
        _bindings.Clear();
        StopMouse();
        NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
