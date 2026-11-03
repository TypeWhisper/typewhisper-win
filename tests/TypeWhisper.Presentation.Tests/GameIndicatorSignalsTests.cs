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
    public void Shown_FollowsWhetherTheLastChordReachedTheGame()
    {
        var results = new Queue<bool>([false, true]);
        var signals = new GameIndicatorSignals(Chords, _ => results.Dequeue());

        // Alt held for push-to-talk: the recording chord is not sent, so TypeWhisper keeps its own indicator.
        signals.Observe(DictationPhase.Recording, recording: true);
        Assert.False(signals.Shown);
        // Released: the transcribing chord arrives, and the game shows the rest.
        signals.Observe(DictationPhase.Processing, recording: false);
        Assert.True(signals.Shown);

        var lost = new GameIndicatorSignals(Chords, chord => chord == Chords.Recording);
        lost.Observe(DictationPhase.Recording, recording: true);
        Assert.True(lost.Shown);
        // The game lost the foreground before transcribing: TypeWhisper's indicator takes over again.
        lost.Observe(DictationPhase.Processing, recording: false);
        Assert.False(lost.Shown);
    }

    private sealed class ManualTime : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Shown_EndsWhenTheGamesBarTimesOut()
    {
        var time = new ManualTime();
        var signals = new GameIndicatorSignals(Chords, _ => true, time);

        signals.Observe(DictationPhase.Recording, recording: true);
        Assert.Equal(time.Now + GameIndicatorSignals.RecordingLimit, signals.ExpiresAt);
        time.Now += GameIndicatorSignals.RecordingLimit;
        // The add-on faded its bar after a very long recording: TypeWhisper's indicator comes back.
        Assert.False(signals.Shown);
        Assert.Null(signals.ExpiresAt);

        signals.Observe(DictationPhase.Processing, recording: false);
        Assert.True(signals.Shown);
        time.Now += GameIndicatorSignals.TranscribingLimit - TimeSpan.FromSeconds(1);
        Assert.True(signals.Shown);
        time.Now += TimeSpan.FromSeconds(2);
        Assert.False(signals.Shown);
    }

    [Fact]
    public void Shown_StaysForAFinishedDictation()
    {
        var time = new ManualTime();
        var signals = new GameIndicatorSignals(Chords, _ => true, time);
        signals.Observe(DictationPhase.Recording, recording: true);
        signals.Observe(DictationPhase.Processing, recording: false);

        signals.Observe(DictationPhase.Completed, recording: false);
        time.Now += TimeSpan.FromHours(1);

        // The add-on completes its bar itself; a late recheck must not bring back TypeWhisper's finished bubble.
        Assert.True(signals.Shown);
        Assert.Null(signals.ExpiresAt);
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
