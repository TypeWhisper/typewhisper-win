using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Windows.Native;

public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private bool _disposed;

    private uint _targetModifiers;
    private uint _targetVk;
    private bool _isPressed;

    public event EventHandler? KeyDown;
    public event EventHandler? KeyUp;
    public bool IsEnabled { get; set; } = true;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void SetHotkey(string hotkeyString)
    {
        if (HotkeyParser.Parse(hotkeyString, out var modifiers, out var vk))
        {
            _targetModifiers = modifiers;
            _targetVk = vk;
        }
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;

        if (module is not null)
        {
            _hookId = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandleW(module.ModuleName),
                0);
        }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _isPressed = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEnabled && (_targetVk != 0 || _targetModifiers != 0))
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vkCode = hookStruct.vkCode;

            var isKeyDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
            var isKeyUp = wParam == NativeMethods.WM_KEYUP || wParam == NativeMethods.WM_SYSKEYUP;

            var winRequired = (_targetModifiers & NativeMethods.MOD_WIN) != 0;
            var isWinKeyEvent = IsWinKey(vkCode);

            // Suppress Windows key immediately if other modifiers are held
            if (winRequired && isWinKeyEvent && isKeyDown && !_isPressed)
            {
                if (AreOtherModifiersPressed())
                {
                    _isPressed = true;
                    KeyDown?.Invoke(this, EventArgs.Empty);
                    return (IntPtr)1;
                }
            }

            if (winRequired && isWinKeyEvent && isKeyUp && _isPressed)
            {
                _isPressed = false;
                KeyUp?.Invoke(this, EventArgs.Empty);
                return (IntPtr)1;
            }

            if (winRequired && isWinKeyEvent && _isPressed)
                return (IntPtr)1;

            if (isKeyDown && !_isPressed)
            {
                if (_targetVk != 0)
                {
                    if (vkCode == _targetVk && AreModifiersPressed())
                    {
                        _isPressed = true;
                        KeyDown?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (!winRequired && IsModifierKey(vkCode) && AreAllModifiersPressed())
                {
                    _isPressed = true;
                    KeyDown?.Invoke(this, EventArgs.Empty);
                }
            }
            else if (isKeyUp && _isPressed && ShouldRelease(vkCode))
            {
                _isPressed = false;
                KeyUp?.Invoke(this, EventArgs.Empty);
            }
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool AreOtherModifiersPressed()
    {
        var ctrlOk = (_targetModifiers & NativeMethods.MOD_CONTROL) == 0 || IsKeyPressed(NativeMethods.VK_CONTROL);
        var shiftOk = (_targetModifiers & NativeMethods.MOD_SHIFT) == 0 || IsKeyPressed(NativeMethods.VK_SHIFT);
        var altOk = (_targetModifiers & NativeMethods.MOD_ALT) == 0 || IsKeyPressed(NativeMethods.VK_MENU);
        return ctrlOk && shiftOk && altOk;
    }

    private bool AreModifiersPressed()
    {
        var ctrlOk = (_targetModifiers & NativeMethods.MOD_CONTROL) == 0 || IsKeyPressed(NativeMethods.VK_CONTROL);
        var shiftOk = (_targetModifiers & NativeMethods.MOD_SHIFT) == 0 || IsKeyPressed(NativeMethods.VK_SHIFT);
        var altOk = (_targetModifiers & NativeMethods.MOD_ALT) == 0 || IsKeyPressed(NativeMethods.VK_MENU);
        var winOk = (_targetModifiers & NativeMethods.MOD_WIN) == 0 ||
                    IsKeyPressed(NativeMethods.VK_LWIN) || IsKeyPressed(NativeMethods.VK_RWIN);
        return ctrlOk && shiftOk && altOk && winOk;
    }

    private bool AreAllModifiersPressed() => AreModifiersPressed();

    private bool ShouldRelease(uint vkCode)
    {
        if (vkCode == _targetVk) return true;
        if ((_targetModifiers & NativeMethods.MOD_CONTROL) != 0 && IsCtrlKey(vkCode)) return true;
        if ((_targetModifiers & NativeMethods.MOD_SHIFT) != 0 && IsShiftKey(vkCode)) return true;
        if ((_targetModifiers & NativeMethods.MOD_ALT) != 0 && IsAltKey(vkCode)) return true;
        if ((_targetModifiers & NativeMethods.MOD_WIN) != 0 && IsWinKey(vkCode)) return true;
        return false;
    }

    private static bool IsKeyPressed(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsCtrlKey(uint vk) =>
        vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL;
    private static bool IsShiftKey(uint vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT;
    private static bool IsAltKey(uint vk) =>
        vk is NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;
    private static bool IsWinKey(uint vk) =>
        vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;
    private static bool IsModifierKey(uint vk) =>
        IsCtrlKey(vk) || IsShiftKey(vk) || IsAltKey(vk) || IsWinKey(vk);

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

internal static class HotkeyParser
{
    public static bool Parse(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(hotkeyString)) return false;

        foreach (var part in hotkeyString.Split('+'))
        {
            var upper = part.Trim().ToUpperInvariant();
            switch (upper)
            {
                case "CTRL" or "CONTROL" or "COMMANDORCONTROL":
                    modifiers |= NativeMethods.MOD_CONTROL; break;
                case "SHIFT":
                    modifiers |= NativeMethods.MOD_SHIFT; break;
                case "ALT":
                    modifiers |= NativeMethods.MOD_ALT; break;
                case "WIN" or "SUPER" or "META":
                    modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    vk = ParseKey(upper);
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0 || modifiers != 0;
    }

    private static uint ParseKey(string key)
    {
        if (key.StartsWith('F') && key.Length is 2 or 3 &&
            int.TryParse(key.AsSpan(1), out var fNum) && fNum is >= 1 and <= 12)
            return (uint)(NativeMethods.VK_F1 + fNum - 1);

        return key switch
        {
            "SPACE" => NativeMethods.VK_SPACE,
            _ when key.Length == 1 && key[0] is >= 'A' and <= 'Z' => (uint)key[0],
            _ when key.Length == 1 && key[0] is >= '0' and <= '9' => (uint)key[0],
            _ => 0
        };
    }
}
