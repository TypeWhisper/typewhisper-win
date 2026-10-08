using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginHost;

// The host owns normalized mono 16 kHz PCM. Partition it before WAV allocation, then keep only
// the current upload in memory. A failed later chunk propagates; partial text is never delivered.
internal static class ChunkedTranscription
{
    private const int SampleRate = 16_000;

    internal static async Task<PluginTranscriptionResult> DecodeAsync(
        ReadOnlyMemory<float> samples, Func<byte[]> encodeWhole, int maximumBytes,
        Func<byte[], CancellationToken, Task<PluginTranscriptionResult>> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var results = new List<(int Offset, PluginTranscriptionResult Result)>();
        var maximumSamples = maximumBytes > 44 ? (maximumBytes - 44) / 2 : int.MaxValue;

        async Task DecodePart(int offset, int count, int depth, bool original)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var wav = original ? encodeWhole() : PcmWaveEncoder.Encode(samples.Span.Slice(offset, count), maximumBytes);
                var result = await send(wav, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                results.Add((offset, result));
            }
            catch (PluginRequestException ex) when (
                (ex.FailureKind == PluginRequestFailureKind.RequestTooLarge || ex.HttpStatusCode == 413)
                && count >= SampleRate * 2 && depth < 20)
            {
                // Providers may enforce a smaller limit than they advertise. Retry two smaller uploads,
                // stopping at one second instead of recursively resending an irreducible request.
                var left = FindBoundary(samples.Span.Slice(offset, count), count / 2);
                await DecodePart(offset, left, depth + 1, original: false).ConfigureAwait(false);
                await DecodePart(offset + left, count - left, depth + 1, original: false).ConfigureAwait(false);
            }
        }

        if (samples.Length <= maximumSamples)
        {
            await DecodePart(0, samples.Length, 0, original: true).ConfigureAwait(false);
        }
        else
        {
            for (var offset = 0; offset < samples.Length;)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = samples.Slice(offset);
                var count = remaining.Length <= maximumSamples
                    ? remaining.Length : FindBoundary(remaining.Span, maximumSamples);
                await DecodePart(offset, count, 0, original: false).ConfigureAwait(false);
                offset += count;
            }
        }

        if (results.Count == 1) return results[0].Result;
        var languages = results.Select(part => part.Result.DetectedLanguage)
            .Where(language => !string.IsNullOrWhiteSpace(language)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new PluginTranscriptionResult(
            string.Join(" ", results.Select(part => part.Result.Text.Trim()).Where(text => text.Length > 0)),
            languages.Length == 1 ? languages[0] : null,
            samples.Length / (double)SampleRate,
            results.All(part => part.Result.NoSpeechProbability.HasValue)
                ? results.Min(part => part.Result.NoSpeechProbability) : null)
        {
            Segments = results.SelectMany(part => part.Result.Segments.Select(segment => segment with
            {
                Start = segment.Start + part.Offset / (double)SampleRate,
                End = segment.End + part.Offset / (double)SampleRate
            })).ToArray(),
            TokenTimings = results.SelectMany(part => part.Result.TokenTimings.Select(token => token with
            {
                StartSeconds = token.StartSeconds + part.Offset / (double)SampleRate,
                EndSeconds = token.EndSeconds + part.Offset / (double)SampleRate
            })).ToArray()
        };
    }

    private static int FindBoundary(ReadOnlySpan<float> samples, int limit)
    {
        // Prefer a quiet 20 ms window in the final second. The windows remain contiguous: no audio
        // is discarded or repeated and timestamps retain the exact source offset.
        const int window = SampleRate / 50;
        var boundary = limit;
        var bestEnergy = 0.0001; // RMS 0.01, matching the capture speech-energy threshold.
        for (var end = limit; end - window >= Math.Max(limit / 2, limit - SampleRate); end -= window)
        {
            double energy = 0;
            foreach (var sample in samples.Slice(end - window, window)) energy += sample * sample;
            energy /= window;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                boundary = end - window / 2;
            }
        }
        return Math.Max(1, boundary);
    }
}
