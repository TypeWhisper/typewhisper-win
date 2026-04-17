using System.Windows.Input;
using TypeWhisper.Windows.Controls;
using TypeWhisper.Windows.Native;

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

        for (var keyValue = (int)Key.F1; keyValue <= (int)Key.F12; keyValue++)
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
    public void ModifierOnly_WinAlt_PressWinThenAlt_DoesNotSwallowAltKeyUp()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, winDown);

        var altDown = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);
        Assert.True(altDown.RaiseKeyDown);
        Assert.True(altDown.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.False(winUp.RaiseKeyUp);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_WinAlt_ReleaseWinFirst_SwallowsOnlyWinKeyUp()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_ALT);

        _ = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: true, isKeyUp: false);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
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
        Assert.True(altDown.RaiseKeyDown);
        Assert.True(altDown.Swallow);

        var altUp = sut.ProcessKeyEvent(NativeMethods.VK_LMENU, isKeyDown: false, isKeyUp: true);
        Assert.True(altUp.RaiseKeyUp);
        Assert.False(altUp.Swallow);

        var ctrlUp = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, ctrlUp);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedFirst_RequestsSyntheticWinRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, winDown);

        var ctrlDown = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);
        Assert.True(ctrlDown.RaiseKeyDown);
        Assert.True(ctrlDown.Swallow);
        Assert.Equal(0u, ctrlDown.SyntheticKeyUpVk);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.True(winUp.Swallow);
        Assert.True(winUp.RaiseKeyUp);
    }

    [Fact]
    public void ModifierOnly_CtrlWin_WhenWinIsPressedAlone_AllowsStandaloneWinTap()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL);

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, winDown);

        var winUp = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true);
        Assert.Equal(default, winUp);
    }

    [Fact]
    public void KeyedHotkey_WinCtrlX_SwallowsTriggerAndWinRelease()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN | NativeMethods.MOD_CONTROL, (uint)'X');

        var winDown = sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false);
        Assert.Equal(default, winDown);
        _ = sut.ProcessKeyEvent(NativeMethods.VK_LCONTROL, isKeyDown: true, isKeyUp: false);

        var xDown = sut.ProcessKeyEvent((uint)'X', isKeyDown: true, isKeyUp: false);
        Assert.True(xDown.RaiseKeyDown);
        Assert.True(xDown.Swallow);

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

        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: false, isKeyUp: true));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: false, isKeyUp: true));
    }

    [Fact]
    public void KeyedHotkey_WinX_AllowsOtherWinShortcutsToPassThrough()
    {
        var sut = CreateStateMachine(NativeMethods.MOD_WIN, (uint)'X');

        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LWIN, isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent(NativeMethods.VK_LSHIFT, isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: true, isKeyUp: false));
        Assert.Equal(default, sut.ProcessKeyEvent((uint)'S', isKeyDown: false, isKeyUp: true));
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
    public void Parser_RejectsSingleModifierOnlyHotkeys(string hotkey)
    {
        Assert.False(HotkeyParser.Parse(hotkey, out _, out _));
        Assert.Equal("", HotkeyParser.Normalize(hotkey));
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

    private static HotkeyMatchStateMachine CreateStateMachine(uint modifiers, uint vk = 0)
    {
        var sut = new HotkeyMatchStateMachine();
        sut.SetHotkey(modifiers, vk);
        return sut;
    }
}
