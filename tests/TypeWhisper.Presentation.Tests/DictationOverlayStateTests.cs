using TypeWhisper.WinUI;
using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationOverlayStateTests
{
    [Theory]
    [InlineData(6, false, 4)]
    [InlineData(6, true, 6)]
    [InlineData(0, true, 0)]
    [InlineData(1, false, 1)]
    [InlineData(2, false, 2)]
    [InlineData(3, false, 3)]
    public void ModelLoadingIsVisibleOnlyAfterADictationAttempt(int phase, bool attempted, int expected)
    {
        Assert.Equal((DictationPhase)expected, DictationOverlayState.VisiblePhase((DictationPhase)phase, attempted));
    }

    [Theory]
    [InlineData(1, true, false, false)]
    [InlineData(2, true, false, false)]
    [InlineData(3, true, false, false)]
    [InlineData(5, true, false, true)]
    [InlineData(5, false, false, false)]
    [InlineData(1, true, true, true)]
    [InlineData(1, false, true, false)]
    [InlineData(4, true, true, false)]
    [InlineData(6, true, true, false)]
    [InlineData(6, true, false, false)]
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
    [InlineData(6, "LOADING MODEL")]
    public void RuntimeStateRetainsRealSessionData(int phase, string label)
    {
        var state = new DictationOverlayState((DictationPhase)phase, TimeSpan.FromSeconds(12), "Session status", "Notepad");
        Assert.Equal(label, state.Label);
        Assert.Equal("Notepad", state.TargetApp);
        Assert.Equal(TimeSpan.FromSeconds(12), state.Duration);
        Assert.Equal("Session status", state.Message);
    }
}
