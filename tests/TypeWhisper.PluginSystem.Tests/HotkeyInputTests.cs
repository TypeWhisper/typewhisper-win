using System.Windows.Input;
using TypeWhisper.Windows.Controls;
using TypeWhisper.Windows.Native;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public class HotkeyInputTests
{
    public static IEnumerable<object[]> RecorderSupportedNonModifierKeys()
    {
        yield return [Key.Space, "Space", (uint)NativeMethods.VK_SPACE];
        yield return [Key.Return, "Enter", (uint)NativeMethods.VK_RETURN];
        yield return [Key.Back, "Backspace", (uint)NativeMethods.VK_BACK];
        yield return [Key.Tab, "Tab", (uint)NativeMethods.VK_TAB];
        yield return [Key.Delete, "Delete", (uint)NativeMethods.VK_DELETE];
        yield return [Key.Insert, "Insert", (uint)NativeMethods.VK_INSERT];
        yield return [Key.Home, "Home", (uint)NativeMethods.VK_HOME];
        yield return [Key.End, "End", (uint)NativeMethods.VK_END];
        yield return [Key.PageUp, "PageUp", (uint)NativeMethods.VK_PRIOR];
        yield return [Key.PageDown, "PageDown", (uint)NativeMethods.VK_NEXT];
        yield return [Key.Up, "Up", (uint)NativeMethods.VK_UP];
        yield return [Key.Down, "Down", (uint)NativeMethods.VK_DOWN];
        yield return [Key.Left, "Left", (uint)NativeMethods.VK_LEFT];
        yield return [Key.Right, "Right", (uint)NativeMethods.VK_RIGHT];
        yield return [Key.PrintScreen, "PrintScreen", (uint)NativeMethods.VK_SNAPSHOT];
        yield return [Key.Pause, "Pause", (uint)NativeMethods.VK_PAUSE];
        yield return [Key.Scroll, "ScrollLock", (uint)NativeMethods.VK_SCROLL];
        yield return [Key.CapsLock, "CapsLock", (uint)NativeMethods.VK_CAPITAL];
        yield return [Key.NumLock, "NumLock", (uint)NativeMethods.VK_NUMLOCK];
        yield return [Key.Apps, "Apps", (uint)NativeMethods.VK_APPS];
        yield return [Key.Sleep, "Sleep", (uint)NativeMethods.VK_SLEEP];
        yield return [Key.Multiply, "NumMultiply", (uint)NativeMethods.VK_MULTIPLY];
        yield return [Key.Add, "NumAdd", (uint)NativeMethods.VK_ADD];
        yield return [Key.Separator, "NumSeparator", (uint)NativeMethods.VK_SEPARATOR];
        yield return [Key.Subtract, "NumSubtract", (uint)NativeMethods.VK_SUBTRACT];
        yield return [Key.Decimal, "NumDecimal", (uint)NativeMethods.VK_DECIMAL];
        yield return [Key.Divide, "NumDivide", (uint)NativeMethods.VK_DIVIDE];
        yield return [Key.BrowserBack, "BrowserBack", (uint)NativeMethods.VK_BROWSER_BACK];
        yield return [Key.BrowserForward, "BrowserForward", (uint)NativeMethods.VK_BROWSER_FORWARD];
        yield return [Key.BrowserRefresh, "BrowserRefresh", (uint)NativeMethods.VK_BROWSER_REFRESH];
        yield return [Key.BrowserStop, "BrowserStop", (uint)NativeMethods.VK_BROWSER_STOP];
        yield return [Key.BrowserSearch, "BrowserSearch", (uint)NativeMethods.VK_BROWSER_SEARCH];
        yield return [Key.BrowserFavorites, "BrowserFavorites", (uint)NativeMethods.VK_BROWSER_FAVORITES];
        yield return [Key.BrowserHome, "BrowserHome", (uint)NativeMethods.VK_BROWSER_HOME];
        yield return [Key.VolumeMute, "VolumeMute", (uint)NativeMethods.VK_VOLUME_MUTE];
        yield return [Key.VolumeDown, "VolumeDown", (uint)NativeMethods.VK_VOLUME_DOWN];
        yield return [Key.VolumeUp, "VolumeUp", (uint)NativeMethods.VK_VOLUME_UP];
        yield return [Key.MediaNextTrack, "MediaNext", (uint)NativeMethods.VK_MEDIA_NEXT_TRACK];
        yield return [Key.MediaPreviousTrack, "MediaPrevious", (uint)NativeMethods.VK_MEDIA_PREV_TRACK];
        yield return [Key.MediaStop, "MediaStop", (uint)NativeMethods.VK_MEDIA_STOP];
        yield return [Key.MediaPlayPause, "MediaPlayPause", (uint)NativeMethods.VK_MEDIA_PLAY_PAUSE];
        yield return [Key.LaunchMail, "LaunchMail", (uint)NativeMethods.VK_LAUNCH_MAIL];
        yield return [Key.SelectMedia, "MediaSelect", (uint)NativeMethods.VK_LAUNCH_MEDIA_SELECT];
        yield return [Key.LaunchApplication1, "LaunchApp1", (uint)NativeMethods.VK_LAUNCH_APP1];
        yield return [Key.LaunchApplication2, "LaunchApp2", (uint)NativeMethods.VK_LAUNCH_APP2];
        yield return [Key.OemTilde, "`", (uint)NativeMethods.VK_OEM_3];
        yield return [Key.OemMinus, "-", (uint)NativeMethods.VK_OEM_MINUS];
        yield return [Key.OemPlus, "=", (uint)NativeMethods.VK_OEM_PLUS];
        yield return [Key.OemOpenBrackets, "[", (uint)NativeMethods.VK_OEM_4];
        yield return [Key.OemCloseBrackets, "]", (uint)NativeMethods.VK_OEM_6];
        yield return [Key.OemSemicolon, ";", (uint)NativeMethods.VK_OEM_1];
        yield return [Key.OemQuotes, "'", (uint)NativeMethods.VK_OEM_7];
        yield return [Key.OemComma, ",", (uint)NativeMethods.VK_OEM_COMMA];
        yield return [Key.OemPeriod, ".", (uint)NativeMethods.VK_OEM_PERIOD];
        yield return [Key.OemQuestion, "/", (uint)NativeMethods.VK_OEM_2];
        yield return [Key.OemBackslash, "\\", (uint)NativeMethods.VK_OEM_5];

        for (var keyValue = (int)Key.A; keyValue <= (int)Key.Z; keyValue++)
        {
            var key = (Key)keyValue;
            var token = key.ToString();
            yield return [key, token, (uint)token[0]];
        }

        for (var keyValue = (int)Key.D0; keyValue <= (int)Key.D9; keyValue++)
        {
            var key = (Key)keyValue;
            var digit = (char)('0' + (key - Key.D0));
            yield return [key, digit.ToString(), (uint)digit];
        }

        for (var keyValue = (int)Key.F1; keyValue <= (int)Key.F24; keyValue++)
        {
            var key = (Key)keyValue;
            yield return [key, key.ToString(), (uint)(NativeMethods.VK_F1 + (key - Key.F1))];
        }

        for (var keyValue = (int)Key.NumPad0; keyValue <= (int)Key.NumPad9; keyValue++)
        {
            var key = (Key)keyValue;
            yield return [key, $"Num{key - Key.NumPad0}", (uint)(NativeMethods.VK_NUMPAD0 + (key - Key.NumPad0))];
        }
    }

    [Fact]
    public void ModifierOnly_WinAlt_TriggersOnFirstRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var altDown = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, altDown);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyDown);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_WinAlt_ReleaseWinFirst_SwallowsOnlyWinKeyUp()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.RaiseKeyDown);
        Assert.True(winUp.RaiseKeyUp);
        Assert.True(winUp.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, altUp);
    }

    [Fact]
    public void ModifierOnly_WinCtrlAlt_ActivatesInArbitraryOrder()
    {
        var sut = CreateStateMachine(
            NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var altDown = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, altDown);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyDown);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedFirst_SuppressesInitialWinKey()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var ctrlDown = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, ctrlDown);
        Assert.Equal(0u, ctrlDown.SyntheticKeyUpVk);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.True(winUp.RaiseKeyDown);
        Assert.True(winUp.RaiseKeyUp);
    }

    [Fact]
    public void ModifierOnly_CtrlShift_TriggersOnReleaseWithoutSwallowingModifiers()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT);

        var ctrlDown = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.False(ctrlDown.Swallow);

        var shiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, shiftDown);

        var shiftUp = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true);
        Assert.True(shiftUp.RaiseKeyDown);
        Assert.True(shiftUp.RaiseKeyUp);
        Assert.False(shiftUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);
    }

    [Fact]
    public void ModifierOnly_CtrlShift_CanTriggerOnPressForHybridBehavior()
    {
        var sut = CreateStateMachine(
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT,
            activateModifierOnlyOnKeyDown: true);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var shiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.True(shiftDown.RaiseKeyDown);

        var shiftUp = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true);
        Assert.True(shiftUp.RaiseKeyUp);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenCtrlIsPressedFirst_SuppressesWinActivationKey()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var ctrlDown = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, ctrlDown);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.False(winDown.RaiseKeyDown);
        Assert.True(winDown.Swallow);
        Assert.Equal(0u, winDown.SyntheticKeyUpVk);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.True(ctrlUp.RaiseKeyDown);
        Assert.True(ctrlUp.RaiseKeyUp);
        Assert.False(ctrlUp.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.False(winUp.RaiseKeyUp);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedAlone_ReplaysStandaloneWinTap()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, winUp.SyntheticKeyTapVk);
        Assert.False(winUp.RaiseKeyUp);
    }

    [Fact]
    public void KeyedHotkey_WinCtrlX_SwallowsTriggerAndWinRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL, (uint)'X');

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var xDown = sut.ProcessKeyEvent((uint)'X', isKeyDown: true, isKeyUp: false);
        Assert.True(xDown.RaiseKeyDown);
        Assert.True(xDown.Swallow);
        Assert.Equal(0u, xDown.SyntheticKeyUpVk);

        var xUp = sut.ProcessKeyEvent((uint)'X', isKeyDown: false, isKeyUp: true);
        Assert.True(xUp.RaiseKeyUp);
        Assert.True(xUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_AllowsOtherWinShortcutsToPassThrough()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var shiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.False(shiftDown.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, shiftDown.SyntheticKeyDownVk);

        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true));
    }

    [Fact]
    public void ResetRuntimeState_AllowsModifierOnlyHotkeyAfterMissedRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        var firstShiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.False(firstShiftDown.RaiseKeyDown);

        sut.ResetRuntimeState();

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        var secondShiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.False(secondShiftDown.RaiseKeyDown);
        var secondShiftUp = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true);
        Assert.True(secondShiftUp.RaiseKeyDown);
        Assert.True(secondShiftUp.RaiseKeyUp);
    }

    [Fact]
    public void KeyedHotkey_WinX_AllowsOtherWinShortcutsToPassThrough()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN, (uint)'X');

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.True(winDown.Swallow);

        var shiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.False(shiftDown.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, shiftDown.SyntheticKeyDownVk);

        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true));
    }

    [Fact]
    public void KeyedHotkey_WinX_DoesNotActivateAfterWinChordWasPassedThrough()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN, (uint)'X');

        Assert.True(sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false).Swallow);

        var shiftDown = sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false);
        Assert.False(shiftDown.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, shiftDown.SyntheticKeyDownVk);

        var xDown = sut.ProcessKeyEvent((uint)'X', isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, xDown);

        Assert.Equal(default, sut.ProcessKeyEvent((uint)'X', isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true));
    }

    [Fact]
    public void RecorderSession_TracksWinModifierWithoutImplicitAlt()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);
        sut.NoteModifierDown(Key.LeftCtrl);

        Assert.Equal(ModifierKeys.Windows | ModifierKeys.Control, sut.GetCurrentModifiers());
        Assert.Equal("Ctrl+Win+K", sut.TryRecordHotkey(Key.K));
    }

    [Fact]
    public void RecorderSession_RecordsModifierOnlyWinAltCombo()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);
        sut.NoteModifierDown(Key.LeftAlt);

        var hotkey = sut.TryRecordModifierOnlyOnRelease(Key.LeftAlt);

        Assert.Equal("Alt+Win", hotkey);
        Assert.Equal(ModifierKeys.Windows, sut.GetCurrentModifiers());
    }

    [Theory]
    [InlineData(Key.LeftCtrl, "Left Ctrl")]
    [InlineData(Key.RightCtrl, "Right Ctrl")]
    [InlineData(Key.LeftShift, "Left Shift")]
    [InlineData(Key.RightShift, "Right Shift")]
    [InlineData(Key.LeftAlt, "Left Alt")]
    [InlineData(Key.RightAlt, "Right Alt")]
    public void RecorderSession_RecordsSideSpecificSingleModifier(Key key, string expectedHotkey)
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(key);

        var hotkey = sut.TryRecordModifierOnlyOnRelease(key);

        Assert.Equal(expectedHotkey, hotkey);
        Assert.Equal(ModifierKeys.None, sut.GetCurrentModifiers());
    }

    [Fact]
    public void KeyboardHook_OnlyIgnoresSelfInjectedInput()
    {
        var selfInjected = new NativeMethods.KBDLLHOOKSTRUCT
        {
            flags = NativeMethods.LLKHF_INJECTED,
            dwExtraInfo = NativeMethods.SelfInjectedInputMarker
        };

        var externalInjected = new NativeMethods.KBDLLHOOKSTRUCT
        {
            flags = NativeMethods.LLKHF_INJECTED,
            dwExtraInfo = IntPtr.Zero
        };

        var hardwareInput = new NativeMethods.KBDLLHOOKSTRUCT
        {
            flags = 0,
            dwExtraInfo = IntPtr.Zero
        };

        Assert.True(KeyboardHook.ShouldIgnoreInjectedInput(selfInjected));
        Assert.False(KeyboardHook.ShouldIgnoreInjectedInput(externalInjected));
        Assert.False(KeyboardHook.ShouldIgnoreInjectedInput(hardwareInput));
    }

    [Theory]
    [InlineData(NativeMethods.VK_RETURN)]
    [InlineData(NativeMethods.VK_TAB)]
    public void TargetAppCorrectionCommitObserver_SignalsHardwareEnterAndTab(int virtualKey)
    {
        var input = new NativeMethods.KBDLLHOOKSTRUCT
        {
            vkCode = (uint)virtualKey
        };

        var result = TargetAppCorrectionCommitObserver.ShouldSignalCommitKey(
            0,
            NativeMethods.WM_KEYDOWN,
            input);

        Assert.True(result);
    }

    [Fact]
    public void TargetAppCorrectionCommitObserver_IgnoresInjectedEnter()
    {
        var input = new NativeMethods.KBDLLHOOKSTRUCT
        {
            vkCode = NativeMethods.VK_RETURN,
            flags = NativeMethods.LLKHF_INJECTED
        };

        var result = TargetAppCorrectionCommitObserver.ShouldSignalCommitKey(
            0,
            NativeMethods.WM_KEYDOWN,
            input);

        Assert.False(result);
    }

    [Fact]
    public void TargetAppCorrectionCommitObserver_IgnoresNonCommitKeys()
    {
        var input = new NativeMethods.KBDLLHOOKSTRUCT
        {
            vkCode = (uint)'A'
        };

        var result = TargetAppCorrectionCommitObserver.ShouldSignalCommitKey(
            0,
            NativeMethods.WM_KEYDOWN,
            input);

        Assert.False(result);
    }

    [Fact]
    public void RecorderSession_DoesNotRecordSingleModifierRelease()
    {
        var sut = new HotkeyRecorderSession();
        sut.NoteModifierDown(Key.LWin);

        var hotkey = sut.TryRecordModifierOnlyOnRelease(Key.LWin);

        Assert.Equal("", hotkey);
        Assert.Equal(ModifierKeys.None, sut.GetCurrentModifiers());
    }

    [Theory]
    [MemberData(nameof(RecorderSupportedNonModifierKeys))]
    public void RecorderAndParser_RoundTripAllSupportedNonModifierKeys(Key key, string expectedToken, uint expectedVk)
    {
        var hotkey = HotkeyRecorderControl.FormatHotkey(ModifierKeys.Control | ModifierKeys.Alt, key);

        Assert.Equal($"Ctrl+Alt+{expectedToken}", hotkey);
        Assert.True(HotkeyParser.Parse(hotkey, out var modifiers, out var vk));
        Assert.Equal(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, modifiers);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("Ctrl+,", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_COMMA)]
    [InlineData("Ctrl+.", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_PERIOD)]
    [InlineData("Win+/", NativeMethods.MOD_WIN, NativeMethods.VK_OEM_2)]
    [InlineData("Ctrl+;", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_1)]
    [InlineData("Alt+'", NativeMethods.MOD_ALT, NativeMethods.VK_OEM_7)]
    [InlineData("Alt+[", NativeMethods.MOD_ALT, NativeMethods.VK_OEM_4)]
    [InlineData("Alt+]", NativeMethods.MOD_ALT, NativeMethods.VK_OEM_6)]
    [InlineData("Ctrl+\\", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_5)]
    [InlineData("Ctrl+-", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_MINUS)]
    [InlineData("Ctrl+=", NativeMethods.MOD_CONTROL, NativeMethods.VK_OEM_PLUS)]
    [InlineData("Ctrl+PageUp", NativeMethods.MOD_CONTROL, NativeMethods.VK_PRIOR)]
    [InlineData("Alt+Left", NativeMethods.MOD_ALT, NativeMethods.VK_LEFT)]
    [InlineData("Win+PrintScreen", NativeMethods.MOD_WIN, NativeMethods.VK_SNAPSHOT)]
    [InlineData("Ctrl+Num3", NativeMethods.MOD_CONTROL, NativeMethods.VK_NUMPAD0 + 3)]
    public void Parser_SupportsRegressionAndNamedKeyCombinations(string hotkey, uint expectedModifiers, uint expectedVk)
    {
        Assert.True(HotkeyParser.Parse(hotkey, out var modifiers, out var vk));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedVk, vk);
    }

    [Theory]
    [InlineData("Esc")]
    [InlineData("Escape")]
    public void Parser_KeepsEscapeAliases(string hotkey)
    {
        Assert.True(HotkeyParser.Parse(hotkey, out var modifiers, out var vk));
        Assert.Equal(0u, modifiers);
        Assert.Equal((uint)NativeMethods.VK_ESCAPE, vk);
    }

    [Theory]
    [InlineData("Ctrl")]
    [InlineData("Shift")]
    [InlineData("Alt")]
    [InlineData("Win")]
    public void Parser_RejectsGenericSingleModifierOnlyHotkeys(string hotkey)
    {
        Assert.False(HotkeyParser.Parse(hotkey, out _, out _));
        Assert.Equal("", HotkeyParser.Normalize(hotkey));
    }

    [Theory]
    [InlineData("Left Ctrl", NativeMethods.VK_LCONTROL)]
    [InlineData("Right Ctrl", NativeMethods.VK_RCONTROL)]
    [InlineData("Left Shift", NativeMethods.VK_LSHIFT)]
    [InlineData("Right Shift", NativeMethods.VK_RSHIFT)]
    [InlineData("Left Alt", NativeMethods.VK_LMENU)]
    [InlineData("Right Alt", NativeMethods.VK_RMENU)]
    public void Parser_AllowsSideSpecificSingleModifierHotkeys(string hotkey, uint expectedVk)
    {
        Assert.True(HotkeyParser.Parse(hotkey, out var modifiers, out var vk));
        Assert.Equal(0u, modifiers);
        Assert.Equal(expectedVk, vk);
        Assert.Equal(hotkey, HotkeyParser.Normalize(hotkey));
    }

    [Theory]
    [InlineData("Ctrl+Shift", NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT)]
    [InlineData("Alt+Win", NativeMethods.MOD_ALT | NativeMethods.MOD_WIN)]
    public void Parser_AllowsModifierOnlyChordsWithTwoOrMoreModifiers(string hotkey, uint expectedModifiers)
    {
        Assert.True(HotkeyParser.Parse(hotkey, out var modifiers, out var vk));
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(0u, vk);
        Assert.Equal(hotkey, HotkeyParser.Normalize(hotkey));
    }

    [Fact]
    public void Parser_RejectsUnknownKeyTokens()
    {
        Assert.False(HotkeyParser.Parse("Ctrl+UnknownKey", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+UnknownKey")]
    public void Parser_NormalizesInvalidRecentActionDefaultsToEmpty(string hotkey)
    {
        Assert.Equal("", HotkeyParser.Normalize(hotkey));
    }

    [Theory]
    [InlineData("MouseLeft", 0)]
    [InlineData("Ctrl+MouseRight", 1)]
    [InlineData("MouseMiddle", 2)]
    [InlineData("MouseBack", 3)]
    [InlineData("MouseForward", 4)]
    public void Parser_RoundTripsMouseButtons(string hotkey, int expectedButton)
    {
        Assert.True(HotkeyParser.Parse(hotkey, out ParsedHotkey parsed));
        Assert.Equal(HotkeyTargetKind.Mouse, parsed.Kind);
        Assert.Equal((HotkeyMouseButton)expectedButton, parsed.MouseButton);
        Assert.Equal(hotkey.StartsWith("Ctrl+", StringComparison.Ordinal) ? NativeMethods.MOD_CONTROL : 0u, parsed.Modifiers);
        Assert.Equal(hotkey, HotkeyParser.Normalize(hotkey));
    }

    [Fact]
    public void KeyedHotkey_RequiresExactModifiers()
    {
        var unmodified = CreateStateMachine(0, NativeMethods.VK_INSERT);
        _ = unmodified.ProcessKeyEvent(NativeMethods.VK_LSHIFT, true, false);
        Assert.Equal(default, unmodified.ProcessKeyEvent(NativeMethods.VK_INSERT, true, false));

        var ctrl = CreateStateMachine(NativeMethods.MOD_CONTROL, (uint)'K');
        _ = ctrl.ProcessKeyEvent(NativeMethods.VK_LCONTROL, true, false);
        _ = ctrl.ProcessKeyEvent(NativeMethods.VK_LSHIFT, true, false);
        Assert.Equal(default, ctrl.ProcessKeyEvent((uint)'K', true, false));
    }

    [Fact]
    public void ModifierOnly_CancelsWhenAnotherKeyJoinsTheChord()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_CONTROL | NativeMethods.MOD_WIN);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, true, false);
        Assert.True(sut.ProcessKeyEvent(NativeMethods.VK_LWIN, true, false).Swallow);

        var otherKey = sut.ProcessKeyEvent((uint)'S', true, false);
        Assert.False(otherKey.RaiseKeyDown);
        Assert.False(otherKey.Swallow);
        Assert.Equal((uint)NativeMethods.VK_LWIN, otherKey.SyntheticKeyDownVk);

        Assert.False(sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, false, true).RaiseKeyDown);
        Assert.False(sut.ProcessKeyEvent(NativeMethods.VK_LWIN, false, true).RaiseKeyDown);
    }

    [Fact]
    public void MouseHotkey_RequiresExactModifiersAndSwallowsMatchedDownAndUp()
    {
        var sut = new MouseHotkeyMatchStateMachine();
        sut.SetHotkey(NativeMethods.MOD_CONTROL, HotkeyMouseButton.Back);

        Assert.Equal(default, sut.ProcessMouseEvent(HotkeyMouseButton.Back, true, false, NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT));
        var down = sut.ProcessMouseEvent(HotkeyMouseButton.Back, true, false, NativeMethods.MOD_CONTROL);
        Assert.True(down.RaiseKeyDown);
        Assert.True(down.Swallow);
        var up = sut.ProcessMouseEvent(HotkeyMouseButton.Back, false, true, NativeMethods.MOD_CONTROL);
        Assert.True(up.RaiseKeyUp);
        Assert.True(up.Swallow);
    }

    [Fact]
    public void MouseHook_MapsXButtonsAndOnlyIgnoresSelfInjectedInput()
    {
        Assert.True(KeyboardHook.TryGetMouseEvent(
            NativeMethods.WM_XBUTTONDOWN,
            NativeMethods.XBUTTON2 << 16,
            out var button,
            out var isDown,
            out var isUp));
        Assert.Equal(HotkeyMouseButton.Forward, button);
        Assert.True(isDown);
        Assert.False(isUp);

        var selfInjected = new NativeMethods.MSLLHOOKSTRUCT
        {
            flags = NativeMethods.LLMHF_INJECTED,
            dwExtraInfo = NativeMethods.SelfInjectedInputMarker
        };
        Assert.True(KeyboardHook.ShouldIgnoreInjectedInput(selfInjected));
        selfInjected.dwExtraInfo = IntPtr.Zero;
        Assert.False(KeyboardHook.ShouldIgnoreInjectedInput(selfInjected));
    }

    [Fact]
    public void Recorder_FormatsMouseButtonWithModifiers()
    {
        Assert.Equal(
            "Ctrl+Alt+MouseBack",
            HotkeyRecorderControl.FormatMouseHotkey(ModifierKeys.Control | ModifierKeys.Alt, "MouseBack"));
    }

    [Fact]
    public void SideSpecificSingleModifier_OnlyActivatesMatchingSide()
    {
        var sut = CreateStateMachine(0, NativeMethods.VK_RCONTROL);

        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true));

        var rightCtrlDown = sut.ProcessKeyEvent(NativeMethods.VK_RCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.False(rightCtrlDown.RaiseKeyDown);
        Assert.False(rightCtrlDown.RaiseKeyUp);
        Assert.False(rightCtrlDown.Swallow);

        var rightCtrlUp = sut.ProcessKeyEvent(NativeMethods.VK_RCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.True(rightCtrlUp.RaiseKeyDown);
        Assert.True(rightCtrlUp.RaiseKeyUp);
        Assert.False(rightCtrlUp.Swallow);
    }

    private static HotkeyMatchStateMachine CreateStateMachine(
        uint modifiers,
        uint vk = 0,
        bool activateModifierOnlyOnKeyDown = false)
    {
        var sut = new HotkeyMatchStateMachine();
        sut.SetHotkey(modifiers, vk, activateModifierOnlyOnKeyDown);
        return sut;
    }
}
