using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.WinUI;

internal sealed class PrototypeHotkeyRegistration : IDisposable
{
    private const int HotkeyId = 0x5457;
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private readonly nuint SubclassId;

    private readonly IntPtr _hwnd;
    private readonly Action _callback;
    private readonly SubclassProc _subclassProc;
    private bool _registered;
    private readonly Dictionary<string, int> _bindings = new();
    private int _nextId = HotkeyId;

    internal string DisplayText { get; private set; } = "Not assigned";
    internal string Value => string.Join(",", _bindings.Keys);

    internal PrototypeHotkeyRegistration(Microsoft.UI.Xaml.Window window, Action callback, int idBase = HotkeyId)
    {
        _nextId = idBase;
        SubclassId = (nuint)idBase;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _callback = callback;
        _subclassProc = WindowSubclassProc;

        if (!SetWindowSubclass(_hwnd, _subclassProc, SubclassId, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to subclass the prototype window.");

        _registered = true;
    }

    internal string? TryChange(string value)
    {
        var requested = PrototypeShortcutRules.Split(value).Select(PrototypeShortcutRules.Normalize).Distinct().ToArray();
        var added = new Dictionary<string, int>();
        foreach (var chord in requested.Where(chord => !_bindings.ContainsKey(chord)))
        {
            var error = PrototypeShortcutRules.Validate(chord, false);
            var parts = chord.Split('+');
            var key = parts[^1] switch { "SPACE" => "Space", "ENTER" => "Enter", "ESC" => "Escape", var other => other };
            if (error is not null || !Enum.TryParse<global::Windows.System.VirtualKey>(key, true, out var vk) || vk == global::Windows.System.VirtualKey.F12)
            {
                foreach (var id in added.Values) UnregisterHotKey(_hwnd, id);
                return error ?? "Choose a letter, function key or Space with modifiers. F12 is reserved.";
            }
            uint mods = ModNoRepeat;
            foreach (var modifier in parts[..^1]) mods |= modifier switch { "ALT" => ModAlt, "CTRL" => ModControl, "SHIFT" => 4u, "WIN" => 8u, _ => 0u };
            var newId = ++_nextId;
            if (!RegisterHotKey(_hwnd, newId, mods, (uint)vk))
            {
                foreach (var id in added.Values) UnregisterHotKey(_hwnd, id);
                return $"{chord} is unavailable or already used by another app. Your previous shortcuts are unchanged.";
            }
            added.Add(chord, newId);
        }
        foreach (var old in _bindings.Keys.Where(key => !requested.Contains(key)).ToArray())
        {
            UnregisterHotKey(_hwnd, _bindings[old]);
            _bindings.Remove(old);
        }
        foreach (var pair in added) _bindings.Add(pair.Key, pair.Value);
        DisplayText = requested.Length == 0 ? "Not assigned" : requested[0].Replace("+", " ");
        return null;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        IntPtr referenceData)
    {
        if (message == WmHotkey && _bindings.ContainsValue(wParam.ToInt32()))
            _callback();

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        _registered = false;
        foreach (var id in _bindings.Values) UnregisterHotKey(_hwnd, id);
        _bindings.Clear();
        RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        IntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hwnd,
        SubclassProc callback,
        nuint subclassId,
        IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
