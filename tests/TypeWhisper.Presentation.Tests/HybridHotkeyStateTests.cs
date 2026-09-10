using TypeWhisper.WinUI;
using TypeWhisper.Presentation;
using Xunit;

public class HybridHotkeyStateTests
{
    [Theory]
    [InlineData(RecordingMode.Toggle, 50, false)]
    [InlineData(RecordingMode.Toggle, 1000, false)]
    [InlineData(RecordingMode.Hold, 50, true)]
    [InlineData(RecordingMode.Hold, 1000, true)]
    public void ExplicitModeDeterminesReleaseRegardlessOfDuration(RecordingMode mode, int duration, bool stop)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        Assert.Null(state.Key(0xA2, true, 0, bindings, mode: mode));
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 0, bindings, mode: mode));
        Assert.Null(state.Key(0xA0, true, 25, bindings, true, mode));
        Assert.Equal(stop ? HybridHotkeyAction.Stop : (HybridHotkeyAction?)null,
            state.Key(0xA0, false, duration, bindings, true, mode));
        Assert.Null(state.Key(0xA2, false, duration + 10, bindings, !stop, mode));
        state.Key(0xA2, true, duration + 20, bindings, !stop, mode);
        Assert.Equal(stop ? HybridHotkeyAction.Start : HybridHotkeyAction.Stop,
            state.Key(0xA0, true, duration + 30, bindings, !stop, mode));
    }

    [Theory]
    [InlineData(RecordingMode.Toggle)]
    [InlineData(RecordingMode.Hold)]
    public void ModeChangeCannotCompleteAnAlreadyHeldChord(RecordingMode mode)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        Assert.Null(state.Key(0xA0, true, 10, bindings, mode: mode));
        Assert.Null(state.Key(0xA0, false, 20, bindings, mode: mode));
        Assert.Null(state.Key(0xA0, true, 30, bindings, mode: mode));
        Assert.Null(state.Key(0xA0, false, 40, bindings, mode: mode));
        Assert.Null(state.Key(0xA2, false, 50, bindings, mode: mode));
        state.Key(0xA2, true, 60, bindings, mode: mode);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 70, bindings, mode: mode));
    }

    [Fact]
    public void ModeChangeOnRepeatedKeyCancelsOwnedGestureAndWaitsForRelease()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        state.Key(0xA0, true, 10, bindings);
        Assert.Equal(HybridHotkeyAction.Cancel, state.Key(0xA0, true, 20, bindings, true, RecordingMode.Toggle));
        Assert.Null(state.Key(0xA0, false, 30, bindings, false, RecordingMode.Toggle));
        Assert.Null(state.Key(0xA2, false, 40, bindings, false, RecordingMode.Toggle));
        state.Key(0xA2, true, 50, bindings, false, RecordingMode.Toggle);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 60, bindings, false, RecordingMode.Toggle));
    }

    [Fact]
    public void HoldGestureDoesNotStopRecordingStartedElsewhere()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings, true, RecordingMode.Hold);
        Assert.Null(state.Key(0xA0, true, 10, bindings, true, RecordingMode.Hold));
        Assert.Null(state.Key(0xA0, false, 20, bindings, true, RecordingMode.Hold));
        Assert.Null(state.Key(0xA2, false, 30, bindings, true, RecordingMode.Hold));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TapStartsImmediatelyAndKeepsRecording(bool functionKey)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { functionKey ? "CTRL+SHIFT+F9" : "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        var action = state.Key(0xA0, true, 0, bindings);
        if (functionKey) { Assert.Null(action); action = state.Key(0x78, true, 0, bindings); }
        Assert.Equal(HybridHotkeyAction.Start, action);
        Assert.Null(state.Key(functionKey ? 0x78 : 0xA0, false, 100, bindings, true));
        Assert.Null(state.Key(0xA2, false, 110, bindings));
        Assert.Null(state.Key(0xA0, false, 120, bindings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HoldStartsAndReleaseStopsWithoutToggle(bool functionKey)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { functionKey ? "CTRL+SHIFT+F9" : "CTRL+SHIFT" };
        state.Key(0xA3, true, 0, bindings);
        var action = state.Key(0xA1, true, 0, bindings);
        if (functionKey) { Assert.Null(action); action = state.Key(0x78, true, 0, bindings); }
        Assert.Equal(HybridHotkeyAction.Start, action);
        Assert.Null(state.Key(functionKey ? 0x78 : 0xA1, true, 700, bindings));
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(functionKey ? 0x78 : 0xA1, false, 1000, bindings));
        Assert.Null(state.Key(0xA3, false, 1100, bindings));
        Assert.Null(state.Key(0xA1, false, 1200, bindings));
    }

    [Fact]
    public void ExtraKeyCancelsPendingModifierGesture()
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        state.Key(0xA0, true, 0, bindings);
        Assert.Equal(HybridHotkeyAction.Cancel, state.Key(0x41, true, 50, bindings));
        Assert.Null(state.Key(0x41, false, 450, bindings));
        Assert.Null(state.Key(0xA0, false, 460, bindings));
        Assert.Null(state.Key(0xA2, false, 470, bindings));
        state.Key(0xA2, true, 500, bindings);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 500, bindings));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void ExistingRecordingStopsOnPressAndNeverRestarts(int duration)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA3, true, 0, bindings, true);
        Assert.Equal(HybridHotkeyAction.Stop, state.Key(0xA1, true, 0, bindings, true));
        Assert.Null(state.Key(0xA1, false, duration, bindings, false));
        Assert.Null(state.Key(0xA3, false, duration + 10, bindings, false));
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public void ThresholdOnlyDeterminesReleaseBehavior(int duration, bool stop)
    {
        var state = new HybridHotkeyState();
        var bindings = new HashSet<string> { "CTRL+SHIFT" };
        state.Key(0xA2, true, 0, bindings);
        Assert.Equal(HybridHotkeyAction.Start, state.Key(0xA0, true, 0, bindings));
        Assert.Equal(stop ? HybridHotkeyAction.Stop : (HybridHotkeyAction?)null,
            state.Key(0xA0, false, duration, bindings, true));
    }
}
