using TypeWhisper.Plugin.Games;
using TypeWhisper.PluginSDK.Models;

public class GameIndicatorSignalsTests
{
    private static readonly GameIndicatorChords Chords = new([0x11, 0x10, 0x77], [0x11, 0x10, 0x78], [0x11, 0x10, 0x79]);

    private static Func<IReadOnlyList<ushort>, bool> Record(List<string> sent, bool delivered = true) => chord =>
    {
        sent.Add(chord[^1] switch { 0x77 => "recording", 0x78 => "transcribing", 0x79 => "stopped", _ => "?" });
        return delivered;
    };

    [Fact]
    public void Dictation_SignalsRecordingAndTranscribing_AndLeavesTheFinishToTheChat()
    {
        var sent = new List<string>();
        var signals = new GameIndicatorSignals(Chords);

        signals.Observe(DictationTargetPhase.Recording, Record(sent));
        signals.Observe(DictationTargetPhase.Recording, Record(sent));
        signals.Observe(DictationTargetPhase.Transcribing, Record(sent));
        signals.Observe(DictationTargetPhase.Delivered, Record(sent));
        signals.Observe(DictationTargetPhase.Stopped, Record(sent));

        Assert.Equal(["recording", "transcribing"], sent);
    }

    [Fact]
    public void Stop_StopsTheBarOnce()
    {
        var sent = new List<string>();
        var signals = new GameIndicatorSignals(Chords);

        signals.Observe(DictationTargetPhase.Recording, Record(sent));
        signals.Observe(DictationTargetPhase.Transcribing, Record(sent));
        // Canceled, failed, copied or handled by a workflow action: TypeWhisper's own confirmation shows.
        Assert.Null(signals.Observe(DictationTargetPhase.Stopped, Record(sent)));
        signals.Observe(DictationTargetPhase.Stopped, Record(sent));
        signals.Observe(DictationTargetPhase.Recording, Record(sent));

        Assert.Equal(["recording", "transcribing", "stopped"], sent);
        Assert.False(signals.Shown);
    }

    [Fact]
    public void NothingSentYet_NothingToStop()
    {
        var sent = new List<string>();
        var signals = new GameIndicatorSignals(Chords);

        signals.Observe(DictationTargetPhase.Stopped, Record(sent));

        Assert.Empty(sent);
    }

    [Fact]
    public void UndeliveredSignals_StillMoveOn()
    {
        // The game was not in front: a later stage must not resend the earlier one.
        var sent = new List<string>();
        var signals = new GameIndicatorSignals(Chords);

        Assert.Null(signals.Observe(DictationTargetPhase.Recording, Record(sent, delivered: false)));
        signals.Observe(DictationTargetPhase.Recording, Record(sent, delivered: false));
        signals.Observe(DictationTargetPhase.Transcribing, Record(sent, delivered: false));

        Assert.Equal(["recording", "transcribing"], sent);
    }

    [Fact]
    public void Shown_FollowsWhetherTheLastChordReachedTheGame()
    {
        var results = new Queue<bool>([false, true]);
        var signals = new GameIndicatorSignals(Chords);

        // Alt held for push-to-talk: the recording chord is not sent, so TypeWhisper keeps its own indicator.
        Assert.Null(signals.Observe(DictationTargetPhase.Recording, _ => results.Dequeue()));
        // Released: the transcribing chord arrives, and the game shows the rest.
        Assert.NotNull(signals.Observe(DictationTargetPhase.Transcribing, _ => results.Dequeue()));
        Assert.True(signals.Shown);

        var lost = new GameIndicatorSignals(Chords);
        Assert.NotNull(lost.Observe(DictationTargetPhase.Recording, chord => chord == Chords.Recording));
        // The game lost the foreground before transcribing: TypeWhisper's indicator takes over again.
        Assert.Null(lost.Observe(DictationTargetPhase.Transcribing, chord => chord == Chords.Recording));
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
        var signals = new GameIndicatorSignals(Chords, time);

        Assert.Equal(time.Now + GameIndicatorSignals.RecordingLimit, signals.Observe(DictationTargetPhase.Recording, _ => true));
        time.Now += GameIndicatorSignals.RecordingLimit;
        // The add-on faded its bar after a very long recording: TypeWhisper's indicator comes back.
        Assert.False(signals.Shown);

        Assert.Equal(time.Now + GameIndicatorSignals.TranscribingLimit, signals.Observe(DictationTargetPhase.Transcribing, _ => true));
        time.Now += GameIndicatorSignals.TranscribingLimit - TimeSpan.FromSeconds(1);
        Assert.True(signals.Shown);
        time.Now += TimeSpan.FromSeconds(2);
        Assert.False(signals.Shown);
    }

    [Fact]
    public void Shown_StaysForADeliveredDictation()
    {
        var time = new ManualTime();
        var signals = new GameIndicatorSignals(Chords, time);
        signals.Observe(DictationTargetPhase.Recording, _ => true);
        signals.Observe(DictationTargetPhase.Transcribing, _ => true);

        // The add-on completes its bar itself; a late recheck must not bring back TypeWhisper's finished bubble.
        Assert.Equal(DateTimeOffset.MaxValue, signals.Observe(DictationTargetPhase.Delivered, _ => true));
        time.Now += TimeSpan.FromHours(1);
        Assert.True(signals.Shown);
    }

    [Fact]
    public void IndicatorFor_NeedsTheAddOnWithItsBarOn()
    {
        var game = GameCatalog.WorldOfWarcraft;
        var addOn = WowAddOnSettings.Default;

        Assert.Equal(game.Indicator, GameChat.IndicatorFor(game, addOn, addOnInstalled: true));
        Assert.Null(GameChat.IndicatorFor(game, addOn with { ShowBar = false }, true));
        Assert.Null(GameChat.IndicatorFor(game, addOn, addOnInstalled: false));
    }
}
