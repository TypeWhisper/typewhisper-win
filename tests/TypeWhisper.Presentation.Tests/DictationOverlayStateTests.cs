using TypeWhisper.WinUI;
using Xunit;

public sealed class DictationOverlayStateTests
{
    [Theory]
    [InlineData(0, "READY")]
    [InlineData(1, "RECORDING")]
    [InlineData(2, "TRANSCRIBING")]
    [InlineData(3, "ERROR")]
    public void RuntimeStateRetainsRealSessionData(int phase, string label)
    {
        var state = new DictationOverlayState((DictationPhase)phase, TimeSpan.FromSeconds(12), "Session status", "Notepad");
        Assert.Equal(label, state.Label);
        Assert.Equal("Notepad", state.TargetApp);
        Assert.Equal(TimeSpan.FromSeconds(12), state.Duration);
        Assert.Equal("Session status", state.Message);
    }
}
