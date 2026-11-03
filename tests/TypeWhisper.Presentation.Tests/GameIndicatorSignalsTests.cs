using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

public class GameIndicatorSignalsTests
{
    private static readonly GameIndicatorChords Chords = new([0x11, 0x10, 0x77], [0x11, 0x10, 0x78], [0x11, 0x10, 0x79]);

    private static (GameIndicatorSignals Signals, List<string> Sent) Create(bool delivered = true)
    {
        var sent = new List<string>();
        return (new GameIndicatorSignals(Chords, chord =>
        {
            sent.Add(chord[^1] switch { 0x77 => "recording", 0x78 => "transcribing", 0x79 => "stopped", _ => "?" });
            return delivered;
        }), sent);
    }

    [Fact]
    public void Dictation_SignalsRecordingAndTranscribing_AndLeavesTheFinishToTheChat()
    {
        var (signals, sent) = Create();

        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Processing, recording: true);
        signals.Observe(DictationPhase.Processing, recording: false);
        signals.Observe(DictationPhase.Completed, recording: false);
        signals.Observe(DictationPhase.Idle, recording: false);

        Assert.Equal(["recording", "transcribing"], sent);
    }

    [Theory]
    [InlineData(nameof(DictationPhase.Idle))]
    [InlineData(nameof(DictationPhase.Error))]
    [InlineData(nameof(DictationPhase.Copied))]
    public void Cancel_Failure_OrCopy_StopsTheBarOnce(string end)
    {
        var (signals, sent) = Create();

        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Processing, recording: false);
        signals.Observe(Enum.Parse<DictationPhase>(end), recording: false);
        signals.Observe(DictationPhase.Idle, recording: false);
        signals.Observe(DictationPhase.Recording, recording: true);

        Assert.Equal(["recording", "transcribing", "stopped"], sent);
    }

    [Fact]
    public void ModelLoading_CountsAsRecordingWhileTheMicrophoneIsOpen()
    {
        var (signals, sent) = Create();

        signals.Observe(DictationPhase.LoadingModel, recording: true);
        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.LoadingModel, recording: false);

        Assert.Equal(["recording", "transcribing"], sent);
    }

    [Fact]
    public void NothingSentYet_NothingToStop()
    {
        var (signals, sent) = Create();

        signals.Observe(DictationPhase.Idle, recording: false);
        signals.Observe(DictationPhase.Error, recording: false);

        Assert.Empty(sent);
    }

    [Fact]
    public void UndeliveredSignals_StillMoveOn()
    {
        // The game was not in front: a later stage must not resend the earlier one.
        var (signals, sent) = Create(delivered: false);

        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Processing, recording: false);

        Assert.Equal(["recording", "transcribing"], sent);
    }

    [Fact]
    public void IndicatorFor_NeedsTheAddOnWithItsBarOn()
    {
        var on = new GamePreferences { WorldOfWarcraft = new() { Enabled = true } };
        var addOn = WowAddOnSettings.Default;

        Assert.Equal(GameCatalog.WorldOfWarcraft.Indicator, on.IndicatorFor("Wow", addOn, addOnInstalled: true));
        Assert.Null(on.IndicatorFor("notepad", addOn, true));
        Assert.Null(new GamePreferences().IndicatorFor("Wow", addOn, true));
        Assert.Null(on.IndicatorFor("Wow", addOn with { ShowBar = false }, true));
        Assert.Null(on.IndicatorFor("Wow", addOn, addOnInstalled: false));
    }
}
