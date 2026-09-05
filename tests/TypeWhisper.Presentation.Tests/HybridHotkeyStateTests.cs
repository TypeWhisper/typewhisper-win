using TypeWhisper.WinUI;
using Xunit;

public class HybridHotkeyStateTests
{
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
