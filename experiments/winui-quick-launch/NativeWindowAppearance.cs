using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

internal static class NativeWindowAppearance
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickframe = 0x00040000L;
    private const long WsSysmenu = 0x00080000L;
    private const long WsMinimizebox = 0x00020000L;
    private const long WsMaximizebox = 0x00010000L;
    private const long WsPopup = unchecked((long)0x80000000L);
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    internal static void RemoveSystemBorder(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickframe | WsSysmenu | WsMinimizebox | WsMaximizebox);
        style |= WsPopup;
        _ = SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);

        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
        var cornerPreference = DwmwcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
    }

    internal static void RemoveOverlayFrame(Window window)
    {
        RemoveSystemBorder(window);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // TransparentTintBackdrop supplies per-pixel alpha, so neither a GDI
        // region nor a DWM corner/border should contribute pixels of its own.
        _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & ~WsPopup;
        _ = SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));
        _ = SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
        var cornerPreference = DwmwcpDoNotRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(
        IntPtr hwnd,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
