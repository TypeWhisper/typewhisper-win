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

    [Fact]
    public void ModifierReleasedBeforeTheQueuedClickIsReplayed()
    {
        // Ctrl was held when the hook matched Ctrl+MouseBack, but is up by the time the UI thread handles the click.
        Assert.Equal(new[] { 0xA2 }, MouseShortcutPresses.ReleasedModifiers("CTRL+MOUSEBACK", _ => false));
        Assert.Empty(MouseShortcutPresses.ReleasedModifiers("CTRL+MOUSEBACK", key => key == 0x11));
        Assert.Equal(new[] { 0xA0, 0x5B }, MouseShortcutPresses.ReleasedModifiers("CTRL+SHIFT+WIN+MOUSEFORWARD", key => key == 0x11));
        Assert.Empty(MouseShortcutPresses.ReleasedModifiers("WIN+MOUSEMIDDLE", key => key == 0x5C));
        Assert.Empty(MouseShortcutPresses.ReleasedModifiers(null, _ => false));
    }

    [Fact]
    public void ReplayedModifierStartsTheChordInTheOrderWindowsSaw()
    {
        // The keyboard hook already handled Ctrl's release; the replay feeds Ctrl, the click and Ctrl's release again.
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };
        Assert.Null(state.Key(0xA2, true, 0, bindings, held: _ => true));
        Assert.Null(state.Key(0xA2, false, 5, bindings, held: _ => true));
        var replay = MouseShortcutPresses.ReleasedModifiers("CTRL+MOUSEBACK", _ => false);
        bool HeldWithReplay(int key) => replay.Contains(key) || key == Back;
        foreach (var modifier in replay) Assert.Null(state.Key(modifier, true, 10, bindings, held: HeldWithReplay));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 10, bindings, held: HeldWithReplay));
    }

    [Theory]
    [InlineData(1000u, 1400u, 1400u)]
    [InlineData(1000u, 900u, 1000u)]
    [InlineData(uint.MaxValue - 100, 200u, 200u)]
    public void ReplayedReleaseKeepsItsRealTime(uint click, uint released, uint expected) =>
        Assert.Equal(expected, MouseShortcutPresses.ReplayReleaseTime(click, released));

    [Fact]
    public void ReplayedReleaseAfterAHoldStopsAHybridGesture()
    {
        // Ctrl+MouseBack held for 400 ms: with the real release time the replayed Ctrl-up ends the hold.
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };
        bool Held(int key) => key is 0xA2 or Back;
        Assert.Null(state.Key(0xA2, true, 0, bindings, held: Held));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, held: Held));
        var release = MouseShortcutPresses.ReplayReleaseTime(0, 400);
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA2, false, release, bindings, true, held: Held));
        Assert.Equal(new[] { 0xA2, 0xA4, 0xA0, 0x5B, 0 }, new[] { 0xA3, 0x12, 0xA1, 0x5C, 0x41 }.Select(MouseShortcutPresses.ReplayKey));
    }

    [Fact]
    public void ReplayedReleaseFollowsAnEarlierClickRelease()
    {
        // Click pressed at 0 and released at 50, Ctrl released at 350, all handled late: a short tap keeps recording.
        var plan = MouseShortcutPresses.PlanReleases([0xA2], 0, 50, _ => 350);
        Assert.Equal((0xA2, 350u, false), Assert.Single(plan));
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+MouseBack") };
        bool Held(int key) => key is 0xA2 or Back;
        Assert.Null(state.Key(0xA2, true, 0, bindings, held: Held));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, held: Held));
        Assert.Null(state.Key(Back, false, 50, bindings, true, held: Held));
        Assert.Null(state.Key(0xA2, false, plan[0].Time, bindings, true, held: Held));
    }

    [Fact]
    public void ReplayedReleaseBeforeTheClickReleaseComesFirst()
    {
        var plan = MouseShortcutPresses.PlanReleases([0xA2, 0xA0], 1000, 1500, key => key == 0xA2 ? 1200u : null);
        Assert.Equal(new[] { (0xA0, 1000u, true), (0xA2, 1200u, true) }, plan);
    }

    [Fact]
    public void SeveralReplayedReleasesFollowTheirOwnOrder()
    {
        // Ctrl+Shift+MouseBack pressed at 0, Shift up at 50, Ctrl up at 350, click up at 400: a short tap keeps recording.
        var plan = MouseShortcutPresses.PlanReleases([0xA2, 0xA0], 0, 400, key => key == 0xA2 ? 350u : 50u);
        Assert.Equal(new[] { 0xA0, 0xA2 }, plan.Select(step => step.Modifier));
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { ShortcutRules.Normalize("Ctrl+Shift+MouseBack") };
        bool Held(int key) => key is 0xA2 or 0xA0 or Back;
        Assert.Null(state.Key(0xA2, true, 0, bindings, held: Held));
        Assert.Null(state.Key(0xA0, true, 0, bindings, held: Held));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(Back, true, 0, bindings, held: Held));
        foreach (var step in plan) Assert.Null(state.Key(step.Modifier, false, step.Time, bindings, true, held: Held));
    }
}
