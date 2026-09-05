using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TypeWhisper.WinUIPrototype;

internal sealed class PrototypeHotkeyRegistration : IDisposable
{
    private const int HotkeyId = 0x5457;
    private const uint WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkSpace = 0x20;
    private const nuint SubclassId = 0x54575052;

    private readonly IntPtr _hwnd;
    private readonly Action _callback;
    private readonly SubclassProc _subclassProc;
    private bool _registered;

    internal string DisplayText { get; }

    internal PrototypeHotkeyRegistration(Microsoft.UI.Xaml.Window window, Action callback)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _callback = callback;
        _subclassProc = WindowSubclassProc;

        if (!SetWindowSubclass(_hwnd, _subclassProc, SubclassId, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to subclass the prototype window.");

        if (RegisterHotKey(_hwnd, HotkeyId, ModAlt | ModNoRepeat, VkSpace))
        {
            DisplayText = "Alt  Space";
        }
        else if (RegisterHotKey(_hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkSpace))
        {
            DisplayText = "Ctrl Alt Space";
        }
        else
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, SubclassId);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The prototype hotkeys are already assigned.");
        }

        _registered = true;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        nuint subclassId,
        IntPtr referenceData)
    {
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
            _callback();

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_registered)
            return;

        _registered = false;
        UnregisterHotKey(_hwnd, HotkeyId);
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
