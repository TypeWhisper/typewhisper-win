using TypeWhisper.WinUI;
using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationOverlayStateTests
{
    [Theory]
    [InlineData(1, true, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(3, true, false, false)]
    [InlineData(5, true, false, true)]
    [InlineData(5, false, false, false)]
    [InlineData(1, true, true, true)]
    [InlineData(1, false, true, false)]
    [InlineData(4, true, true, false)]
    public void TranscriptWindowRespectsLiveCapabilityWithoutHidingCompletedOutput(int phase, bool enabled, bool supported, bool visible)
    {
        var state = new DictationOverlayState((DictationPhase)phase, TimeSpan.Zero, "Status", "Notepad");
        Assert.Equal(visible, state.ShouldShowTranscript(enabled, supported));
    }

    [Theory]
    [InlineData(RecordingMode.Hybrid, "Hybrid")]
    [InlineData(RecordingMode.Toggle, "Toggle")]
    [InlineData(RecordingMode.Hold, "Hold")]
    public void RuntimeStateLabelsSelectedRecordingMode(RecordingMode mode, string label)
    {
        var state = new DictationOverlayState(DictationPhase.Recording, TimeSpan.Zero, "Recording", "Notepad", RecordingMode: mode);
        Assert.Equal(label, state.RecordingModeLabel);
    }

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
