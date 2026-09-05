using TypeWhisper.WinUI;
using Xunit;

public class ModifierChordStateTests
{
    private static readonly HashSet<string> Bindings = ["CTRL+SHIFT"];
    [Fact]
    public void FiresOnceOnFullRelease()
    {
        var state = new ModifierChordState();
        Assert.Null(state.Process(0xA2, true, Bindings));
        Assert.Null(state.Process(0xA0, true, Bindings));
        Assert.Null(state.Process(0xA0, true, Bindings));
        Assert.Null(state.Process(0xA2, false, Bindings));
        Assert.Equal("CTRL+SHIFT", state.Process(0xA0, false, Bindings));
        Assert.Null(state.Process(0xA0, false, Bindings));
    }
    [Theory]
    [InlineData(0x78)]
    [InlineData(0x41)]
    [InlineData(0xA4)]
    public void LargerChordsDoNotFire(int extra)
    {
        var state = new ModifierChordState();
        state.Process(0xA2, true, Bindings);
        state.Process(0xA0, true, Bindings);
        state.Process(extra, true, Bindings);
        Assert.Null(state.Process(extra, false, Bindings));
        Assert.Null(state.Process(0xA0, false, Bindings));
        Assert.Null(state.Process(0xA2, false, Bindings));
    }
    [Fact]
    public void RightSideAndReverseOrderWork()
    {
        var state = new ModifierChordState();
        state.Process(0xA1, true, Bindings);
        state.Process(0xA3, true, Bindings);
        state.Process(0xA1, false, Bindings);
        Assert.Equal("CTRL+SHIFT", state.Process(0xA3, false, Bindings));
    }
    [Fact]
    public void RemovedBindingDoesNotArm()
    {
        var state = new ModifierChordState();
        state.Process(0xA2, true, new HashSet<string>());
        state.Process(0xA0, true, new HashSet<string>());
        state.Process(0xA2, false, Bindings);
        Assert.Null(state.Process(0xA0, false, Bindings));
    }
}
