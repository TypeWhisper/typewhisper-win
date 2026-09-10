namespace TypeWhisper.Plugin.ParakeetCtc;

public static class CtcVocabularyScorer
{
    // Viterbi CTC alignment of a complete token sequence inside a bounded
    // time window. Prefix/suffix frames may be skipped; repeated labels require
    // a blank separator. No textual score is substituted for acoustic evidence.
    public static double Score(CtcEmission emission, IReadOnlyList<int> tokens, int blank,
        int startFrame, int endFrame, CancellationToken cancellation)
    {
        if (tokens.Count == 0 || tokens.Any(t => t < 0 || t >= emission.VocabularySize || t == blank)) return double.NegativeInfinity;
        var states = tokens.Count * 2 + 1;
        var previous = Enumerable.Repeat(double.NegativeInfinity, states).ToArray();
        previous[0] = 0;
        var current = new double[states];
        var best = double.NegativeInfinity;
        for (var frame = Math.Max(0, startFrame); frame < Math.Min(emission.Frames, endFrame); frame++)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Fill(current, double.NegativeInfinity); current[0] = 0;
            for (var s = 1; s < states; s++)
            {
                var label = s % 2 == 0 ? blank : tokens[s / 2];
                var incoming = Math.Max(previous[s], previous[s - 1]);
                if (s > 1 && s % 2 != 0 && tokens[s / 2] != tokens[s / 2 - 1]) incoming = Math.Max(incoming, previous[s - 2]);
                current[s] = incoming + emission.LogProbabilities[frame * emission.VocabularySize + label];
            }
            best = Math.Max(best, Math.Max(current[^1], current[^2]));
            (previous, current) = (current, previous);
        }
        // Match the per-token normalization of FluidAudio constrained scoring.
        return best / tokens.Count;
    }
}
