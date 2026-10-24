using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public class MouseShortcutTests
{
    private const int Middle = 0x04, Back = 0x05, Forward = 0x06;

    [Theory]
    [InlineData(Middle, "MouseMiddle")]
    [InlineData(Back, "MouseBack")]
    [InlineData(Forward, "MouseForward")]
    public void MouseButtonsUseTheTokensOfEarlierVersions(int key, string token)
    {
        Assert.Equal(token, ShortcutKeys.Token(key));
        Assert.True(ShortcutKeys.TryParse(token, out var parsed));
        Assert.Equal(key, parsed);
        Assert.True(ShortcutKeys.TryParse(token.ToLowerInvariant(), out _));
        Assert.True(ShortcutKeys.IsMouseChord("Ctrl+" + token));
        Assert.Equal("CTRL+" + token.ToUpperInvariant(), ShortcutRules.Normalize("control+" + token));
    }

    [Theory]
    [InlineData("MouseBack")]
    [InlineData("MouseForward")]
    [InlineData("MouseMiddle")]
    [InlineData("Ctrl+Shift+MouseBack")]
    public void MiddleAndSideButtonsWorkAloneOrWithModifiers(string chord)
    {
        Assert.Null(ShortcutRules.Validate(chord, allowModifiersOnly: true));
        Assert.Null(ShortcutRules.Validate(chord, allowModifiersOnly: false));
        Assert.Null(ShortcutRules.AltGrConflict("Ctrl+Alt+MouseBack", (_, _) => 'x'));
    }

    [Theory]
    [InlineData("MouseLeft")]
    [InlineData("MouseRight")]
    [InlineData("Ctrl+MouseLeft")]
    [InlineData("VK1")]
    [InlineData("Ctrl+VK2")]
    public void PrimaryButtonsAreNeverShortcuts(string chord)
    {
        Assert.NotNull(ShortcutRules.Validate(chord, allowModifiersOnly: true));
        Assert.False(ShortcutKeys.IsMouseChord(chord));
    }

    [Fact]
    public void RawVirtualKeyCodesOfThePrimaryButtonsDoNotParse()
    {
        Assert.False(ShortcutKeys.TryParse("VK1", out _));
        Assert.False(ShortcutKeys.TryParse("VK2", out _));
        Assert.True(ShortcutKeys.TryParse("VK5", out var back));
        Assert.Equal(Back, back);
    }

    [Fact]
    public void MouseButtonsShowAReadableName()
    {
        Assert.Equal("Ctrl + Mouse back button", ShortcutKeys.Display("Ctrl+MouseBack"));
        Assert.Equal("Middle mouse button", ShortcutKeys.Label("MOUSEMIDDLE"));
        Assert.Equal("Mouse forward button", ShortcutKeys.Label("MouseForward"));
    }

    [Theory]
    [InlineData(0x207, 0u, Middle, true)]
    [InlineData(0x208, 0u, Middle, false)]
    [InlineData(0x20B, 0x10000u, Back, true)]
    [InlineData(0x20C, 0x10000u, Back, false)]
    [InlineData(0x20B, 0x20000u, Forward, true)]
    [InlineData(0x20C, 0x20000u, Forward, false)]
    public void HookMessagesBecomeButtonPresses(long message, uint mouseData, int key, bool down)
    {
        Assert.True(MouseShortcutPresses.IsButtonMessage(message));
        Assert.True(MouseShortcutPresses.TryDecode(message, mouseData, out var decoded, out var pressed));
        Assert.Equal(key, decoded);
        Assert.Equal(down, pressed);
    }

    [Theory]
    [InlineData(0x200, 0u)] // move
    [InlineData(0x201, 0u)] // left down
    [InlineData(0x204, 0u)] // right down
    [InlineData(0x20A, 0x780000u)] // wheel
    [InlineData(0x20B, 0x30000u)] // unknown side button
    public void OtherMouseEventsAreIgnored(long message, uint mouseData) =>
        Assert.False(MouseShortcutPresses.TryDecode(message, mouseData, out _, out _));

    [Fact]
    public void ShortcutOwnsTheWholeClickAndNothingElse()
    {
        var presses = new MouseShortcutPresses();
        var chords = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };

        // Without Ctrl the back button keeps navigating in the focused app.
        Assert.False(presses.Button(Back, true, "", chords, true, out var chord));
        Assert.Null(chord);
        Assert.False(presses.Button(Back, false, "", chords, true, out _));

        Assert.True(presses.Button(Back, true, "CTRL", chords, true, out chord));
        Assert.Equal("CTRL+MOUSEBACK", chord);
        // The release belongs to the shortcut even after Ctrl was let go first.
        Assert.True(presses.Button(Back, false, "", chords, true, out chord));
        Assert.Null(chord);

        Assert.False(presses.Button(Forward, true, "CTRL", chords, true, out _));
    }

    [Fact]
    public void PausedShortcutsLeaveClicksWithTheFocusedApp()
    {
        var presses = new MouseShortcutPresses();
        var chords = new HashSet<string> { "MOUSEFORWARD" };
        Assert.False(presses.Button(Forward, true, "", chords, false, out var chord));
        Assert.Null(chord);
        Assert.False(presses.Button(Forward, false, "", chords, true, out _));
    }

    [Theory]
    [InlineData(RecordingMode.Hold, 1000, true)]
    [InlineData(RecordingMode.Hybrid, 1000, true)]
    [InlineData(RecordingMode.Hybrid, 100, false)]
    [InlineData(RecordingMode.Toggle, 1000, false)]
    public void SideButtonDictatesLikeAKey(RecordingMode mode, int duration, bool stopOnRelease)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("MouseBack") };
        var held = new HashSet<int>();
        bool Held(int key) => held.Contains(key);

        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, mode: mode, held: Held));
        held.Add(Back);
        Assert.Equal(stopOnRelease ? HybridHotkeyAction.Stop : (HybridHotkeyAction?)null,
            state.Key(Back, false, duration, bindings, true, mode, held: Held));
        held.Remove(Back);
        Assert.Equal(stopOnRelease ? HybridHotkeyAction.Start : HybridHotkeyAction.Stop,
            state.Key(Back, true, duration + 50, bindings, !stopOnRelease, mode, held: Held));
    }

    [Fact]
    public void ModifierWithSideButtonStartsDictation()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseForward") };
        Assert.Null(state.Key(0xA2, true, 0, bindings));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Forward, true, 10, bindings));
        Assert.Null(state.Key(Forward, false, 50, bindings, true));
        Assert.Null(state.Key(0xA2, false, 60, bindings, true));
    }

    [Fact]
    public void HeldMouseButtonIsNotTakenForALostRelease()
    {
        // Mouse buttons don't repeat. A long middle-button drag must keep blocking another shortcut.
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT", ShortcutRules.Normalize("Alt+MouseMiddle") };
        var held = new HashSet<int>();
        bool Held(int key) => held.Contains(key);
        Assert.Null(state.Key(Middle, true, 0, bindings, held: Held));
        held.Add(Middle);
        Assert.Null(state.Key(0xA2, true, 2000, bindings, held: Held));
        Assert.Empty(state.DroppedKeys);
        held.Add(0xA2);
        Assert.Null(state.Key(0xA0, true, 2010, bindings, held: Held));
    }

    // The dictation hook handles queued clicks before any later keyboard event, so the gesture sees input in
    // the order Windows saw it. These sequences pin the results of that order.
    [Fact]
    public void ShortCtrlClickTapKeepsRecordingWhenCtrlIsReleasedLater()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };
        var held = new HashSet<int>();
        bool Held(int key) => held.Contains(key);
        held.Add(0xA2);
        Assert.Null(state.Key(0xA2, true, 0, bindings, held: Held));
        held.Add(Back);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, held: Held));
        Assert.Null(state.Key(Back, false, 50, bindings, true, held: Held));
        held.Remove(Back);
        Assert.Null(state.Key(0xA2, false, 350, bindings, true, held: Held));
    }

    [Fact]
    public void ReleasingCtrlWhileTheButtonIsHeldStopsAHold()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };
        bool Held(int key) => key is 0xA2 or Back;
        Assert.Null(state.Key(0xA2, true, 0, bindings, mode: RecordingMode.Hold, held: Held));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, mode: RecordingMode.Hold, held: Held));
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA2, false, 350, bindings, true, RecordingMode.Hold, held: Held));
    }
}
