using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Windows.Native;

public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private bool _disposed;
    private readonly HotkeyMatchStateMachine _stateMachine = new();

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
            _stateMachine.SetHotkey(modifiers, vk);
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
        _stateMachine.Reset();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEnabled && _stateMachine.HasHotkey)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if ((hookStruct.flags & NativeMethods.LLKHF_INJECTED) != 0)
                return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);

            var vkCode = hookStruct.vkCode;

            var isKeyDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
            var isKeyUp = wParam == NativeMethods.WM_KEYUP || wParam == NativeMethods.WM_SYSKEYUP;

            var result = _stateMachine.ProcessKeyEvent(vkCode, isKeyDown, isKeyUp);
            if (result.SyntheticKeyTapVk != 0)
                SendSyntheticKeyTap((ushort)result.SyntheticKeyTapVk);
            if (result.SyntheticKeyUpVk != 0)
                SendSyntheticKeyUp((ushort)result.SyntheticKeyUpVk);
            if (result.RaiseKeyDown)
                KeyDown?.Invoke(this, EventArgs.Empty);
            if (result.RaiseKeyUp)
                KeyUp?.Invoke(this, EventArgs.Empty);
            if (result.Swallow)
                return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void SendSyntheticKeyTap(ushort vk)
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk
                    }
                }
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk,
                        dwFlags = NativeMethods.KEYEVENTF_KEYUP
                    }
                }
            }
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendSyntheticKeyUp(ushort vk)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = NativeMethods.KEYEVENTF_KEYUP
                }
            }
        };

        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

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

internal readonly record struct HotkeyProcessResult(
    bool RaiseKeyDown,
    bool RaiseKeyUp,
    bool Swallow,
    uint SyntheticKeyTapVk = 0,
    uint SyntheticKeyUpVk = 0);

internal sealed class HotkeyMatchStateMachine
{
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _pendingSuppressedKeyUps = [];
    private readonly HashSet<uint> _suppressedKeyDowns = [];
    private uint _pendingSuppressedWinKey;

    private uint _targetModifiers;
    private uint _targetVk;
    private bool _isPressed;

    public bool HasHotkey => _targetVk != 0 || _targetModifiers != 0;

    public void SetHotkey(uint modifiers, uint vk)
    {
        _targetModifiers = modifiers;
        _targetVk = vk;
        ResetRuntimeState();
    }

    public void Reset()
    {
        _targetModifiers = 0;
        _targetVk = 0;
        ResetRuntimeState();
    }

    public HotkeyProcessResult ProcessKeyEvent(uint vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (!HasHotkey || (!isKeyDown && !isKeyUp))
            return default;

        var swallow = false;
        var raiseKeyDown = false;
        var raiseKeyUp = false;

        if (isKeyUp && _pendingSuppressedKeyUps.Remove(vkCode))
            swallow = true;

        if (isKeyDown)
        {
            var firstPress = _pressedKeys.Add(vkCode);
            var preSuppressedWinDown = false;

            if (!_isPressed && firstPress && ShouldPreSuppressWinKeyDown(vkCode))
            {
                swallow = true;
                preSuppressedWinDown = true;
                _pendingSuppressedWinKey = vkCode;
                _suppressedKeyDowns.Add(vkCode);
            }

            if (!_isPressed)
            {
                if (ShouldActivate(vkCode))
                {
                    _isPressed = true;
                    raiseKeyDown = true;
                    swallow = true;
                    _suppressedKeyDowns.Add(vkCode);
                    CaptureWinKeyUpsForSuppression();

                    if ((_targetModifiers & NativeMethods.MOD_WIN) != 0
                        && !HotkeyKeyClassifier.IsWinKey(vkCode)
                        && TryGetUnsuppressedPressedWinKey(out var unsuppressedWinKey))
                    {
                        _pendingSuppressedKeyUps.Add(unsuppressedWinKey);
                        _suppressedKeyDowns.Add(unsuppressedWinKey);
                    }

                    if (preSuppressedWinDown)
                        _pendingSuppressedWinKey = 0;
                }
            }
            else if (!firstPress && ShouldSuppressWhilePressed(vkCode))
            {
                swallow = true;
            }

            if (swallow)
                _suppressedKeyDowns.Add(vkCode);
        }
        else if (isKeyUp)
        {
            if (!_isPressed && _pendingSuppressedWinKey == vkCode)
            {
                _pendingSuppressedWinKey = 0;
                _pressedKeys.Remove(vkCode);
                _suppressedKeyDowns.Remove(vkCode);
                return new HotkeyProcessResult(raiseKeyDown, raiseKeyUp, true, vkCode);
            }

            if (_isPressed && ShouldRelease(vkCode))
            {
                _isPressed = false;
                raiseKeyUp = true;

                if (_targetVk == vkCode || HotkeyKeyClassifier.IsWinKey(vkCode))
                    swallow = true;
            }

            _pressedKeys.Remove(vkCode);
            _suppressedKeyDowns.Remove(vkCode);
            if (_pendingSuppressedWinKey == vkCode)
                _pendingSuppressedWinKey = 0;
        }

        return new HotkeyProcessResult(raiseKeyDown, raiseKeyUp, swallow);
    }

    private void ResetRuntimeState()
    {
        _pressedKeys.Clear();
        _pendingSuppressedKeyUps.Clear();
        _suppressedKeyDowns.Clear();
        _pendingSuppressedWinKey = 0;
        _isPressed = false;
    }

    private bool ShouldActivate(uint vkCode)
    {
        if (_targetVk != 0)
            return vkCode == _targetVk && AreRequiredModifiersPressed();

        return HotkeyKeyClassifier.IsModifierKey(vkCode)
            && IsRequiredModifier(vkCode)
            && AreRequiredModifiersPressed();
    }

    private bool ShouldRelease(uint vkCode)
    {
        if (_targetVk != 0 && vkCode == _targetVk)
            return true;

        return RequiredModifierReleased(vkCode);
    }

    private bool RequiredModifierReleased(uint vkCode)
    {
        if ((_targetModifiers & NativeMethods.MOD_CONTROL) != 0
            && HotkeyKeyClassifier.IsCtrlKey(vkCode)
            && !WouldModifierRemainPressedAfterRelease(HotkeyModifier.Control, vkCode))
        {
            return true;
        }

        if ((_targetModifiers & NativeMethods.MOD_SHIFT) != 0
            && HotkeyKeyClassifier.IsShiftKey(vkCode)
            && !WouldModifierRemainPressedAfterRelease(HotkeyModifier.Shift, vkCode))
        {
            return true;
        }

        if ((_targetModifiers & NativeMethods.MOD_ALT) != 0
            && HotkeyKeyClassifier.IsAltKey(vkCode)
            && !WouldModifierRemainPressedAfterRelease(HotkeyModifier.Alt, vkCode))
        {
            return true;
        }

        if ((_targetModifiers & NativeMethods.MOD_WIN) != 0
            && HotkeyKeyClassifier.IsWinKey(vkCode)
            && !WouldModifierRemainPressedAfterRelease(HotkeyModifier.Win, vkCode))
        {
            return true;
        }

        return false;
    }

    private bool AreRequiredModifiersPressed()
    {
        var ctrlOk = (_targetModifiers & NativeMethods.MOD_CONTROL) == 0 || AnyPressed(HotkeyModifier.Control);
        var shiftOk = (_targetModifiers & NativeMethods.MOD_SHIFT) == 0 || AnyPressed(HotkeyModifier.Shift);
        var altOk = (_targetModifiers & NativeMethods.MOD_ALT) == 0 || AnyPressed(HotkeyModifier.Alt);
        var winOk = (_targetModifiers & NativeMethods.MOD_WIN) == 0 || AnyPressed(HotkeyModifier.Win);
        return ctrlOk && shiftOk && altOk && winOk;
    }

    private bool IsRequiredModifier(uint vkCode) =>
        ((_targetModifiers & NativeMethods.MOD_CONTROL) != 0 && HotkeyKeyClassifier.IsCtrlKey(vkCode))
        || ((_targetModifiers & NativeMethods.MOD_SHIFT) != 0 && HotkeyKeyClassifier.IsShiftKey(vkCode))
        || ((_targetModifiers & NativeMethods.MOD_ALT) != 0 && HotkeyKeyClassifier.IsAltKey(vkCode))
        || ((_targetModifiers & NativeMethods.MOD_WIN) != 0 && HotkeyKeyClassifier.IsWinKey(vkCode));

    private bool ShouldSuppressWhilePressed(uint vkCode) =>
        vkCode == _targetVk
        || ((_targetModifiers & NativeMethods.MOD_WIN) != 0 && HotkeyKeyClassifier.IsWinKey(vkCode));

    private bool ShouldPreSuppressWinKeyDown(uint vkCode)
    {
        // Don't suppress the Win key before we know the exact combination.
        // This preserves system shortcuts like Win+S and Win+Shift+S.
        return false;
    }

    private void CaptureWinKeyUpsForSuppression()
    {
        if ((_targetModifiers & NativeMethods.MOD_WIN) == 0)
            return;

        foreach (var pressedKey in _pressedKeys)
        {
            if (HotkeyKeyClassifier.IsWinKey(pressedKey))
                _pendingSuppressedKeyUps.Add(pressedKey);
        }
    }

    private bool TryGetUnsuppressedPressedWinKey(out uint winVkCode)
    {
        foreach (var pressedKey in _pressedKeys)
        {
            if (HotkeyKeyClassifier.IsWinKey(pressedKey) && !_suppressedKeyDowns.Contains(pressedKey))
            {
                winVkCode = pressedKey;
                return true;
            }
        }

        winVkCode = 0;
        return false;
    }

    private bool AnyPressed(HotkeyModifier modifier) =>
        _pressedKeys.Any(vkCode => HotkeyKeyClassifier.MatchesModifier(vkCode, modifier));

    private bool WouldModifierRemainPressedAfterRelease(HotkeyModifier modifier, uint releasedVkCode) =>
        _pressedKeys.Any(vkCode => vkCode != releasedVkCode && HotkeyKeyClassifier.MatchesModifier(vkCode, modifier));
}

internal enum HotkeyModifier
{
    Control,
    Shift,
    Alt,
    Win
}

internal static class HotkeyKeyClassifier
{
    public static bool IsCtrlKey(uint vk) =>
        vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL;

    public static bool IsShiftKey(uint vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT;

    public static bool IsAltKey(uint vk) =>
        vk is NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;

    public static bool IsWinKey(uint vk) =>
        vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    public static bool IsModifierKey(uint vk) =>
        IsCtrlKey(vk) || IsShiftKey(vk) || IsAltKey(vk) || IsWinKey(vk);

    public static bool MatchesModifier(uint vkCode, HotkeyModifier modifier) => modifier switch
    {
        HotkeyModifier.Control => IsCtrlKey(vkCode),
        HotkeyModifier.Shift => IsShiftKey(vkCode),
        HotkeyModifier.Alt => IsAltKey(vkCode),
        HotkeyModifier.Win => IsWinKey(vkCode),
        _ => false
    };
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
            "ESC" or "ESCAPE" => NativeMethods.VK_ESCAPE,
            "SPACE" => NativeMethods.VK_SPACE,
            _ when key.Length == 1 && key[0] is >= 'A' and <= 'Z' => (uint)key[0],
            _ when key.Length == 1 && key[0] is >= '0' and <= '9' => (uint)key[0],
            _ => 0
        };
    }
}
