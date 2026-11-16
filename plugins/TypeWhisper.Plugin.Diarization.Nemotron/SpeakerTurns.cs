using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>
/// Turns 10 ms speaker probabilities into speaker turns the way FluidAudio does it for the macOS app: a speaker is
/// active in a frame when its probability is above 0.5, and turns shorter than 0.2 s are dropped. Speakers are
/// labelled <c>S1</c>, <c>S2</c>, … in the order they first speak.
/// </summary>
internal static class SpeakerTurns
{
    internal const float Threshold = 0.5f;
    internal const double MinimumTurnSeconds = 0.2;
    internal const double FrameSeconds = 0.01;

    /// <summary>The turns of [frames × speakers] row-major probabilities, sorted by start.</summary>
    internal static IReadOnlyList<PluginSpeakerTurn> FromProbabilities(float[] probabilities, int speakers = NemotronStreamingDiarizer.Speakers)
    {
        var frames = probabilities.Length / speakers;
        var runs = new List<(int Speaker, int Start, int End, double Confidence)>();
        for (var s = 0; s < speakers; s++)
        {
            var start = -1;
            var sum = 0.0;
            for (var f = 0; f <= frames; f++)
            {
                var p = f < frames ? probabilities[(f * speakers) + s] : 0f;
                var active = f < frames && p > Threshold;
                if (active)
                {
                    if (start < 0) { start = f; sum = 0; }
                    sum += p;
                }
                else if (start >= 0)
                {
                    if ((f - start) * FrameSeconds >= MinimumTurnSeconds - 1e-9) runs.Add((s, start, f, sum / (f - start)));
                    start = -1;
                }
            }
        }

        var labels = new Dictionary<int, string>();
        return runs.OrderBy(run => run.Start).ThenBy(run => run.Speaker).Select(run =>
        {
            if (!labels.TryGetValue(run.Speaker, out var label)) labels.Add(run.Speaker, label = "S" + (labels.Count + 1));
            return new PluginSpeakerTurn(label, Math.Round(run.Start * FrameSeconds, 2), Math.Round(run.End * FrameSeconds, 2))
                { Confidence = Math.Round(run.Confidence, 3) };
        }).ToArray();
    }
}
