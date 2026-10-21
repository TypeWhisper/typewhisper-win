namespace TypeWhisper.Plugin.GigaAm;

/// <summary>
/// Cuts long recordings into chunks the model handles well and joins their transcripts, as the macOS plugin does with
/// PluginAudioChunking.
/// </summary>
internal static class GigaAmChunking
{
    /// <summary>The model was trained on short utterances; longer recordings are cut at quiet points.</summary>
    internal const int MaximumChunkSeconds = 20;
    // 20 ms energy frames, compared over a 200 ms stretch.
    private const int FrameLength = GigaAmRuntime.SampleRate / 50;
    private const int QuietStretchFrames = 10;
    private const string SentenceEnds = ".!?…";

    /// <summary>
    /// Splits the samples into the fewest chunks of at most <paramref name="maximumChunkSamples"/> samples, each cut at
    /// the quietest stretch within 5 % of the chunk length around an even split.
    /// </summary>
    internal static IReadOnlyList<(int Offset, int Count)> ChunkRanges(ReadOnlySpan<float> samples, int maximumChunkSamples)
    {
        var count = samples.Length;
        if (maximumChunkSamples <= 0 || count <= maximumChunkSamples) return [(0, count)];

        var searchRadius = maximumChunkSamples / 20;
        var ranges = new List<(int Offset, int Count)>();
        var start = 0;
        while (count - start > maximumChunkSamples)
        {
            var remaining = count - start;
            var chunkCount = (remaining + maximumChunkSamples - 1) / maximumChunkSamples;
            var evenCut = start + remaining / chunkCount;
            // Cutting earlier than this would leave more than the remaining chunks can hold and add a chunk.
            var earliestCut = count - (chunkCount - 1) * maximumChunkSamples;
            var searchStart = Math.Max(evenCut - searchRadius, earliestCut);
            var searchEnd = Math.Min(evenCut + searchRadius, start + maximumChunkSamples);
            var cut = searchStart >= searchEnd ? evenCut : QuietestPoint(samples, searchStart, searchEnd);
            ranges.Add((start, cut - start));
            start = cut;
        }
        ranges.Add((start, count - start));
        return ranges;
    }

    /// <summary>The middle of the 200 ms stretch with the least energy between <paramref name="start"/> and <paramref name="end"/>.</summary>
    internal static int QuietestPoint(ReadOnlySpan<float> samples, int start, int end)
    {
        var frameCount = (end - start) / FrameLength;
        if (frameCount <= 0) return start + (end - start) / 2;

        var energies = new double[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            double energy = 0;
            foreach (var sample in samples.Slice(start + frame * FrameLength, FrameLength))
                energy += (double)sample * sample;
            energies[frame] = energy;
        }

        var stretch = Math.Min(QuietStretchFrames, frameCount);
        double window = 0;
        for (var frame = 0; frame < stretch; frame++) window += energies[frame];
        var quietestEnergy = window;
        var quietestStart = 0;
        for (var frame = stretch; frame < frameCount; frame++)
        {
            window += energies[frame] - energies[frame - stretch];
            if (window < quietestEnergy)
            {
                quietestEnergy = window;
                quietestStart = frame - stretch + 1;
            }
        }
        return start + quietestStart * FrameLength + stretch * FrameLength / 2;
    }

    /// <summary>
    /// GigaAM capitalizes the first word of every chunk. When the previous chunk ended without closing a sentence, the
    /// cut fell mid-sentence, so that word is lowercased. A proper noun right after such a cut loses its capital, which
    /// is rarer than an ordinary word there; abbreviations such as "США" keep theirs.
    /// </summary>
    internal static string Continuing(string transcript, string text)
    {
        if (transcript.Length == 0 || SentenceEnds.Contains(transcript[^1]) || text.Length < 2
            || !char.IsUpper(text[0]) || !char.IsLower(text[1]))
            return text;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>Appends a chunk transcript, separated by one space.</summary>
    internal static string Append(string transcript, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return transcript;
        return transcript.Length == 0 ? text : transcript + " " + text;
    }
}
