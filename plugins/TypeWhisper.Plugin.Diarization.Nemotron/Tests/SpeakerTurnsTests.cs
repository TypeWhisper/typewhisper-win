using TypeWhisper.Plugin.Diarization.Nemotron;

namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

public sealed class SpeakerTurnsTests
{
    private static float[] Probabilities(int frames, params (int Speaker, int From, int To, float Value)[] active)
    {
        var probabilities = new float[frames * 8];
        foreach (var (speaker, from, to, value) in active)
            for (var f = from; f < to; f++) probabilities[(f * 8) + speaker] = value;
        return probabilities;
    }

    [Fact]
    public void SpeakersAreNamedInTheOrderTheyFirstSpeak()
    {
        var turns = SpeakerTurns.FromProbabilities(Probabilities(300, (5, 100, 150, 0.9f), (2, 10, 60, 0.7f), (5, 200, 260, 0.8f)));

        Assert.Equal(["S1", "S2", "S2"], turns.Select(turn => turn.SpeakerLabel));
        Assert.Equal([(0.1, 0.6), (1.0, 1.5), (2.0, 2.6)], turns.Select(turn => (turn.Start, turn.End)));
        Assert.Equal(0.7, turns[0].Confidence!.Value, 3);
    }

    [Fact]
    public void ShortTurnsAndProbabilitiesAtTheThresholdAreDropped()
    {
        var turns = SpeakerTurns.FromProbabilities(Probabilities(100,
            (0, 0, 19, 0.9f), (1, 20, 40, 0.9f), (2, 50, 90, 0.5f), (3, 80, 100, 0.6f)));

        // 0.19 s is too short; 0.2 s is kept; exactly 0.5 is not active; a turn may end with the audio.
        Assert.Equal([("S1", 0.2, 0.4), ("S2", 0.8, 1.0)], turns.Select(turn => (turn.SpeakerLabel, turn.Start, turn.End)));
    }

    [Fact]
    public void OverlappingSpeakersKeepTheirOwnTurns()
    {
        var turns = SpeakerTurns.FromProbabilities(Probabilities(100, (0, 0, 80, 0.9f), (1, 40, 100, 0.9f)));

        Assert.Equal([("S1", 0.0, 0.8), ("S2", 0.4, 1.0)], turns.Select(turn => (turn.SpeakerLabel, turn.Start, turn.End)));
        Assert.Empty(SpeakerTurns.FromProbabilities(new float[800]));
    }
}
