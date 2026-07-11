using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Windows.Native;

/// <summary>
/// Provides keyboard hook behavior.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private IntPtr _mouseHookId = IntPtr.Zero;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private bool _disposed;
    private readonly HotkeyMatchStateMachine _stateMachine = new();
    private readonly MouseHotkeyMatchStateMachine _mouseStateMachine = new();
    private HotkeyTargetKind _targetKind;

    /// <summary>
    /// Raised when the key down event occurs.
    /// </summary>
    public event EventHandler? KeyDown;
    /// <summary>
    /// Raised when the key up event occurs.
    /// </summary>
    public event EventHandler? KeyUp;
    /// <summary>
    /// Gets or sets the is enabled value.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Initializes a new instance of the KeyboardHook class.
    /// </summary>
    public KeyboardHook()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    /// <summary>
    /// Sets hotkey.
    /// </summary>
    public void SetHotkey(string hotkeyString)
    {
        _stateMachine.Reset();
        _mouseStateMachine.Reset();

        if (HotkeyParser.Parse(hotkeyString, out ParsedHotkey parsed))
        {
            _targetKind = parsed.Kind;
            if (parsed.Kind == HotkeyTargetKind.Mouse)
                _mouseStateMachine.SetHotkey(parsed.Modifiers, parsed.MouseButton);
            else
                _stateMachine.SetHotkey(parsed.Modifiers, parsed.Code);
        }
    }

    /// <summary>
    /// Starts the service or session.
    /// </summary>
    public void Start()
    {
        if (_keyboardHookId != IntPtr.Zero || _mouseHookId != IntPtr.Zero) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;

        if (module is not null)
        {
            var moduleHandle = NativeMethods.GetModuleHandleW(module.ModuleName);
            if (_targetKind == HotkeyTargetKind.Mouse && _mouseStateMachine.HasHotkey)
            {
                _mouseHookId = NativeMethods.SetWindowsMouseHookExW(
                    NativeMethods.WH_MOUSE_LL,
                    _mouseProc,
                    moduleHandle,
                    0);
            }
            else if (_stateMachine.HasHotkey)
            {
                _keyboardHookId = NativeMethods.SetWindowsHookExW(
                    NativeMethods.WH_KEYBOARD_LL,
                    _keyboardProc,
                    moduleHandle,
                    0);
            }
        }
    }

    /// <summary>
    /// Stops the service or session.
    /// </summary>
    public void Stop()
    {
        if (_keyboardHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }
        if (_mouseHookId != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHookId);
            _mouseHookId = IntPtr.Zero;
        }
        _stateMachine.Reset();
        _mouseStateMachine.Reset();
    }

    internal void ResetRuntimeState()
    {
        _stateMachine.ResetRuntimeState();
        _mouseStateMachine.ResetRuntimeState();
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _stateMachine.HasHotkey)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (ShouldIgnoreInjectedInput(hookStruct))
                return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            var vkCode = hookStruct.vkCode;

            var isKeyDown = wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN;
            var isKeyUp = wParam == NativeMethods.WM_KEYUP || wParam == NativeMethods.WM_SYSKEYUP;

            var result = _stateMachine.ProcessKeyEvent(vkCode, isKeyDown, isKeyUp);
            if (IsEnabled)
            {
                if (result.SyntheticKeyDownVk != 0)
                    SendSyntheticKeyDown((ushort)result.SyntheticKeyDownVk);
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
        }

        return NativeMethods.CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _mouseStateMachine.HasHotkey)
        {
            var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            if (!ShouldIgnoreInjectedInput(hookStruct)
                && TryGetMouseEvent(wParam, hookStruct.mouseData, out var button, out var isDown, out var isUp))
            {
                var result = _mouseStateMachine.ProcessMouseEvent(
                    button,
                    isDown,
                    isUp,
                    GetCurrentModifiers());
                if (IsEnabled)
                {
                    if (result.RaiseKeyDown)
                        KeyDown?.Invoke(this, EventArgs.Empty);
                    if (result.RaiseKeyUp)
                        KeyUp?.Invoke(this, EventArgs.Empty);
                    if (result.Swallow)
                        return (IntPtr)1;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    internal static bool ShouldIgnoreInjectedInput(in NativeMethods.KBDLLHOOKSTRUCT hookStruct) =>
        (hookStruct.flags & NativeMethods.LLKHF_INJECTED) != 0
        && hookStruct.dwExtraInfo == NativeMethods.SelfInjectedInputMarker;

    internal static bool ShouldIgnoreInjectedInput(in NativeMethods.MSLLHOOKSTRUCT hookStruct) =>
        (hookStruct.flags & NativeMethods.LLMHF_INJECTED) != 0
        && hookStruct.dwExtraInfo == NativeMethods.SelfInjectedInputMarker;

    internal static bool TryGetMouseEvent(
        IntPtr message,
        uint mouseData,
        out HotkeyMouseButton button,
        out bool isDown,
        out bool isUp)
    {
        isDown = message == NativeMethods.WM_LBUTTONDOWN
            || message == NativeMethods.WM_RBUTTONDOWN
            || message == NativeMethods.WM_MBUTTONDOWN
            || message == NativeMethods.WM_XBUTTONDOWN;
        isUp = message == NativeMethods.WM_LBUTTONUP
            || message == NativeMethods.WM_RBUTTONUP
            || message == NativeMethods.WM_MBUTTONUP
            || message == NativeMethods.WM_XBUTTONUP;

        button = message.ToInt32() switch
        {
            NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => HotkeyMouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => HotkeyMouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => HotkeyMouseButton.Middle,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP
                when mouseData >> 16 == NativeMethods.XBUTTON1 => HotkeyMouseButton.Back,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP
                when mouseData >> 16 == NativeMethods.XBUTTON2 => HotkeyMouseButton.Forward,
            _ => (HotkeyMouseButton)(-1)
        };
        return (isDown || isUp) && (int)button >= 0;
    }

    private static uint GetCurrentModifiers()
    {
        var modifiers = 0u;
        if (IsKeyDown(NativeMethods.VK_CONTROL)) modifiers |= NativeMethods.MOD_CONTROL;
        if (IsKeyDown(NativeMethods.VK_SHIFT)) modifiers |= NativeMethods.MOD_SHIFT;
        if (IsKeyDown(NativeMethods.VK_MENU)) modifiers |= NativeMethods.MOD_ALT;
        if (IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN)) modifiers |= NativeMethods.MOD_WIN;
        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

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
                        wVk = vk,
                        dwExtraInfo = NativeMethods.SelfInjectedInputMarker
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
                        dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                        dwExtraInfo = NativeMethods.SelfInjectedInputMarker
                    }
                }
            }
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SendSyntheticKeyDown(ushort vk)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwExtraInfo = NativeMethods.SelfInjectedInputMarker
                }
            }
        };

        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
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
                    dwFlags = NativeMethods.KEYEVENTF_KEYUP,
                    dwExtraInfo = NativeMethods.SelfInjectedInputMarker
                }
            }
        };

        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
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
    uint SyntheticKeyDownVk = 0,
    uint SyntheticKeyTapVk = 0,
    uint SyntheticKeyUpVk = 0);

internal sealed class MouseHotkeyMatchStateMachine
{
    private uint _targetModifiers;
    private HotkeyMouseButton _targetButton;
    private bool _hasHotkey;
    private bool _isPressed;

    public bool HasHotkey => _hasHotkey;

    public void SetHotkey(uint modifiers, HotkeyMouseButton button)
    {
        _targetModifiers = modifiers;
        _targetButton = button;
        _hasHotkey = true;
        ResetRuntimeState();
    }

    public HotkeyProcessResult ProcessMouseEvent(
        HotkeyMouseButton button,
        bool isButtonDown,
        bool isButtonUp,
        uint currentModifiers)
    {
        if (!_hasHotkey || button != _targetButton)
            return default;

        if (isButtonDown)
        {
            if (_isPressed)
                return new HotkeyProcessResult(false, false, true);
            if (currentModifiers != _targetModifiers)
                return default;

            _isPressed = true;
            return new HotkeyProcessResult(true, false, true);
        }

        if (isButtonUp && _isPressed)
        {
            _isPressed = false;
            return new HotkeyProcessResult(false, true, true);
        }

        return default;
    }

    public void Reset()
    {
        _targetModifiers = 0;
        _targetButton = default;
        _hasHotkey = false;
        ResetRuntimeState();
    }

    public void ResetRuntimeState() => _isPressed = false;
}

internal sealed class HotkeyMatchStateMachine
{
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _pendingSuppressedKeyUps = [];
    private readonly HashSet<uint> _suppressedKeyDowns = [];

    private uint _pendingSuppressedWinKey;
    private uint _targetModifiers;
    private uint _targetVk;
    private bool _isPressed;
    private bool _winPassThroughActive;
    private bool _modifierOnlyReady;
    private bool _modifierOnlyCanceled;

    /// <summary>
    /// Gets whether has hotkey.
    /// </summary>
    public bool HasHotkey => _targetVk != 0 || _targetModifiers != 0;

    /// <summary>
    /// Sets hotkey.
    /// </summary>
    public void SetHotkey(uint modifiers, uint vk)
    {
        _targetModifiers = modifiers;
        _targetVk = vk;
        ResetRuntimeState();
    }

    /// <summary>
    /// Performs reset.
    /// </summary>
    public void Reset()
    {
        _targetModifiers = 0;
        _targetVk = 0;
        ResetRuntimeState();
    }

    /// <summary>
    /// Processes key event.
    /// </summary>
    public HotkeyProcessResult ProcessKeyEvent(uint vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (!HasHotkey || (!isKeyDown && !isKeyUp))
            return default;

        if (IsModifierOnly)
            return ProcessModifierOnlyKeyEvent(vkCode, isKeyDown, isKeyUp);

        var swallow = false;
        var raiseKeyDown = false;
        var raiseKeyUp = false;
        uint syntheticKeyDownVk = 0;
        uint syntheticKeyTapVk = 0;
        uint syntheticKeyUpVk = 0;

        if (isKeyUp && _pendingSuppressedKeyUps.Remove(vkCode))
            swallow = true;

        if (isKeyDown)
        {
            var firstPress = _pressedKeys.Add(vkCode);

            if (!_isPressed && firstPress && ShouldPreSuppressWinKeyDown(vkCode))
            {
                swallow = true;
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
                    _pendingSuppressedWinKey = 0;
                    CaptureWinKeyUpsForSuppression();

                    if ((_targetModifiers & NativeMethods.MOD_WIN) != 0
                        && !HotkeyKeyClassifier.IsWinKey(vkCode)
                        && TryGetUnsuppressedPressedWinKey(out var unsuppressedWinKey))
                    {
                        _pendingSuppressedKeyUps.Add(unsuppressedWinKey);
                        _suppressedKeyDowns.Add(unsuppressedWinKey);
                        syntheticKeyUpVk = unsuppressedWinKey;
                    }
                }
                else if (!firstPress && _suppressedKeyDowns.Contains(vkCode))
                {
                    swallow = true;
                }
                else if (firstPress && ShouldReleasePendingSuppressedWinKey(vkCode, out var suppressedWinKey))
                {
                    syntheticKeyDownVk = suppressedWinKey;
                    _suppressedKeyDowns.Remove(suppressedWinKey);
                    _pendingSuppressedWinKey = 0;
                    _winPassThroughActive = true;
                }
            }
            else if (!firstPress && (ShouldSuppressWhilePressed(vkCode) || _suppressedKeyDowns.Contains(vkCode)))
            {
                swallow = true;
            }

            if (swallow)
                _suppressedKeyDowns.Add(vkCode);
        }
        else if (isKeyUp)
        {
            var suppressedKeyDown = _suppressedKeyDowns.Contains(vkCode);
            if (!_isPressed && _pendingSuppressedWinKey == vkCode)
            {
                _pendingSuppressedWinKey = 0;
                _pressedKeys.Remove(vkCode);
                _suppressedKeyDowns.Remove(vkCode);
                return new HotkeyProcessResult(raiseKeyDown, raiseKeyUp, true, SyntheticKeyTapVk: vkCode);
            }

            if (_isPressed && ShouldRelease(vkCode))
            {
                _isPressed = false;
                raiseKeyUp = true;

                if (_targetVk == vkCode || HotkeyKeyClassifier.IsWinKey(vkCode) || suppressedKeyDown)
                    swallow = true;
            }

            _pressedKeys.Remove(vkCode);
            _suppressedKeyDowns.Remove(vkCode);
            if (_pendingSuppressedWinKey == vkCode)
                _pendingSuppressedWinKey = 0;
            if (HotkeyKeyClassifier.IsWinKey(vkCode))
                _winPassThroughActive = false;
        }

        return new HotkeyProcessResult(
            raiseKeyDown,
            raiseKeyUp,
            swallow,
            SyntheticKeyDownVk: syntheticKeyDownVk,
            SyntheticKeyTapVk: syntheticKeyTapVk,
            SyntheticKeyUpVk: syntheticKeyUpVk);
    }

    private HotkeyProcessResult ProcessModifierOnlyKeyEvent(uint vkCode, bool isKeyDown, bool isKeyUp)
    {
        var swallow = false;
        var raiseKeyDown = false;
        var raiseKeyUp = false;
        uint syntheticKeyDownVk = 0;
        uint syntheticKeyTapVk = 0;

        if (isKeyUp && _pendingSuppressedKeyUps.Remove(vkCode))
            swallow = true;

        if (isKeyDown)
        {
            var firstPress = _pressedKeys.Add(vkCode);
            if (!firstPress)
                return new HotkeyProcessResult(false, false, _suppressedKeyDowns.Contains(vkCode));

            if (ShouldPreSuppressWinKeyDown(vkCode))
            {
                swallow = true;
                _pendingSuppressedWinKey = vkCode;
                _suppressedKeyDowns.Add(vkCode);
            }

            if (HasUnexpectedModifierOnlyKey())
            {
                _modifierOnlyCanceled = true;
                _modifierOnlyReady = false;
                if (_pendingSuppressedWinKey != 0 && vkCode != _pendingSuppressedWinKey)
                {
                    syntheticKeyDownVk = _pendingSuppressedWinKey;
                    _suppressedKeyDowns.Remove(_pendingSuppressedWinKey);
                    _pendingSuppressedWinKey = 0;
                }
            }
            else if (!_modifierOnlyCanceled && AreModifierOnlyTargetsPressed())
            {
                _modifierOnlyReady = true;
            }
        }
        else if (isKeyUp)
        {
            var suppressedKeyDown = _suppressedKeyDowns.Contains(vkCode);
            if (_modifierOnlyReady && IsModifierOnlyRelease(vkCode))
            {
                _modifierOnlyReady = false;
                _modifierOnlyCanceled = true;
                raiseKeyDown = true;
                raiseKeyUp = true;

                if (_pendingSuppressedWinKey != 0)
                {
                    if (_pendingSuppressedWinKey != vkCode)
                        _pendingSuppressedKeyUps.Add(_pendingSuppressedWinKey);
                    _pendingSuppressedWinKey = 0;
                }

                swallow |= suppressedKeyDown;
            }
            else if (_pendingSuppressedWinKey == vkCode)
            {
                _pendingSuppressedWinKey = 0;
                syntheticKeyTapVk = vkCode;
                swallow = true;
            }

            _pressedKeys.Remove(vkCode);
            _suppressedKeyDowns.Remove(vkCode);
            if (!HasAnyModifierOnlyTargetPressed())
            {
                _modifierOnlyReady = false;
                _modifierOnlyCanceled = false;
            }
        }

        return new HotkeyProcessResult(
            raiseKeyDown,
            raiseKeyUp,
            swallow,
            SyntheticKeyDownVk: syntheticKeyDownVk,
            SyntheticKeyTapVk: syntheticKeyTapVk);
    }

    internal void ResetRuntimeState()
    {
        _pressedKeys.Clear();
        _pendingSuppressedKeyUps.Clear();
        _suppressedKeyDowns.Clear();
        _pendingSuppressedWinKey = 0;
        _isPressed = false;
        _winPassThroughActive = false;
        _modifierOnlyReady = false;
        _modifierOnlyCanceled = false;
    }

    private bool ShouldActivate(uint vkCode)
    {
        if (_winPassThroughActive && (_targetModifiers & NativeMethods.MOD_WIN) != 0)
            return false;

        if (_targetVk != 0)
            return vkCode == _targetVk && GetPressedModifiers() == _targetModifiers;

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

    private bool IsModifierOnly =>
        _targetVk == 0 || (_targetModifiers == 0 && HotkeyKeyClassifier.IsModifierKey(_targetVk));

    private bool HasUnexpectedModifierOnlyKey() =>
        _pressedKeys.Any(vkCode => !IsModifierOnlyTargetKey(vkCode));

    private bool AreModifierOnlyTargetsPressed()
    {
        if (_targetVk != 0)
            return _pressedKeys.Contains(_targetVk) && !HasUnexpectedModifierOnlyKey();

        return AreRequiredModifiersPressed()
            && GetPressedModifiers() == _targetModifiers
            && !HasUnexpectedModifierOnlyKey();
    }

    private bool IsModifierOnlyTargetKey(uint vkCode)
    {
        if (_targetVk != 0)
            return vkCode == _targetVk;
        return IsRequiredModifier(vkCode);
    }

    private bool IsModifierOnlyRelease(uint vkCode)
    {
        if (_targetVk != 0)
            return vkCode == _targetVk;
        return RequiredModifierReleased(vkCode);
    }

    private bool HasAnyModifierOnlyTargetPressed() =>
        _pressedKeys.Any(IsModifierOnlyTargetKey);

    private uint GetPressedModifiers()
    {
        var modifiers = 0u;
        if (AnyPressed(HotkeyModifier.Control)) modifiers |= NativeMethods.MOD_CONTROL;
        if (AnyPressed(HotkeyModifier.Shift)) modifiers |= NativeMethods.MOD_SHIFT;
        if (AnyPressed(HotkeyModifier.Alt)) modifiers |= NativeMethods.MOD_ALT;
        if (AnyPressed(HotkeyModifier.Win)) modifiers |= NativeMethods.MOD_WIN;
        return modifiers;
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
        if (!HotkeyKeyClassifier.IsWinKey(vkCode))
            return false;

        if ((_targetModifiers & NativeMethods.MOD_WIN) == 0)
            return false;

        return _targetVk != 0 || (_targetModifiers & ~NativeMethods.MOD_WIN) != 0;
    }

    private bool ShouldReleasePendingSuppressedWinKey(uint vkCode, out uint suppressedWinKey)
    {
        suppressedWinKey = _pendingSuppressedWinKey;
        if (suppressedWinKey == 0 || vkCode == suppressedWinKey)
            return false;

        return !IsRequiredModifier(vkCode);
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
    /// <summary>
    /// Returns whether ctrl key.
    /// </summary>
    public static bool IsCtrlKey(uint vk) =>
        vk is NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL;

    /// <summary>
    /// Returns whether shift key.
    /// </summary>
    public static bool IsShiftKey(uint vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT;

    /// <summary>
    /// Returns whether alt key.
    /// </summary>
    public static bool IsAltKey(uint vk) =>
        vk is NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;

    /// <summary>
    /// Returns whether win key.
    /// </summary>
    public static bool IsWinKey(uint vk) =>
        vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    /// <summary>
    /// Returns whether modifier key.
    /// </summary>
    public static bool IsModifierKey(uint vk) =>
        IsCtrlKey(vk) || IsShiftKey(vk) || IsAltKey(vk) || IsWinKey(vk);

    /// <summary>
    /// Performs matches modifier.
    /// </summary>
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
    /// <summary>
    /// Performs normalize.
    /// </summary>
    public static string Normalize(string? hotkeyString)
    {
        var normalized = hotkeyString?.Trim() ?? "";
        return Parse(normalized, out ParsedHotkey _) ? normalized : "";
    }

    public static bool IsModifierOnly(string? hotkeyString) =>
        Parse(hotkeyString ?? "", out ParsedHotkey parsed) && parsed.IsModifierOnly;

    /// <summary>
    /// Parses the supplied value into the expected representation.
    /// </summary>
    public static bool Parse(string hotkeyString, out uint modifiers, out uint vk)
    {
        var parsed = Parse(hotkeyString, out ParsedHotkey result);
        modifiers = result.Modifiers;
        vk = result.Kind == HotkeyTargetKind.Keyboard ? result.Code : 0;
        return parsed && result.Kind == HotkeyTargetKind.Keyboard;
    }

    public static bool Parse(string hotkeyString, out ParsedHotkey result)
    {
        var modifiers = 0u;
        var code = 0u;
        var kind = HotkeyTargetKind.Keyboard;
        result = default;
        if (string.IsNullOrWhiteSpace(hotkeyString)) return false;

        if (TryParseSideSpecificSingleModifier(hotkeyString.Trim(), out code))
        {
            result = new ParsedHotkey(0, HotkeyTargetKind.Keyboard, code);
            return true;
        }

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
                    if (HotkeyKeyMap.TryGetMouseButton(part.Trim(), out var mouseButton))
                    {
                        if (code != 0 || kind == HotkeyTargetKind.Mouse)
                            return false;
                        kind = HotkeyTargetKind.Mouse;
                        code = (uint)mouseButton + 1;
                        break;
                    }

                    var vk = ParseKey(part.Trim());
                    if (vk == 0 || code != 0 || kind == HotkeyTargetKind.Mouse)
                        return false;
                    code = vk;
                    break;
            }
        }

        if (code == 0)
        {
            if (CountModifiers(modifiers) < 2)
                return false;
            result = new ParsedHotkey(modifiers, HotkeyTargetKind.Keyboard, 0);
            return true;
        }

        result = new ParsedHotkey(modifiers, kind, code);
        return true;
    }

    private static bool TryParseSideSpecificSingleModifier(string hotkeyString, out uint vk)
    {
        vk = hotkeyString.ToUpperInvariant() switch
        {
            "LEFT CTRL" => NativeMethods.VK_LCONTROL,
            "RIGHT CTRL" => NativeMethods.VK_RCONTROL,
            "LEFT SHIFT" => NativeMethods.VK_LSHIFT,
            "RIGHT SHIFT" => NativeMethods.VK_RSHIFT,
            "LEFT ALT" => NativeMethods.VK_LMENU,
            "RIGHT ALT" => NativeMethods.VK_RMENU,
            _ => 0
        };

        return vk != 0;
    }

    private static uint ParseKey(string key) =>
        HotkeyKeyMap.TryGetVirtualKey(key, out var virtualKey) ? virtualKey : 0;

    private static int CountModifiers(uint modifiers)
    {
        var count = 0;
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) count++;
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) count++;
        if ((modifiers & NativeMethods.MOD_ALT) != 0) count++;
        if ((modifiers & NativeMethods.MOD_WIN) != 0) count++;
        return count;
    }
}

internal enum HotkeyTargetKind
{
    Keyboard,
    Mouse
}

internal readonly record struct ParsedHotkey(uint Modifiers, HotkeyTargetKind Kind, uint Code)
{
    public bool IsModifierOnly =>
        Kind == HotkeyTargetKind.Keyboard
        && (Code == 0 || (Modifiers == 0 && HotkeyKeyClassifier.IsModifierKey(Code)));

    public HotkeyMouseButton MouseButton => (HotkeyMouseButton)(Code - 1);
}
