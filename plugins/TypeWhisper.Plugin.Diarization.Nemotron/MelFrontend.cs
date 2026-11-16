namespace TypeWhisper.Plugin.Diarization.Nemotron;

/// <summary>
/// Log-mel features of Nemotron 3 Diarization, a port of Transformers' NemotronAsrStreamingFeatureExtractor (the same
/// front end as NeMo's AudioToMelSpectrogramPreprocessor at inference): pre-emphasis 0.97, 25 ms symmetric Hann window
/// in a 512-point FFT, 10 ms hop, centered frames with zero padding, power spectrum, 128 Slaney mel bands from 0 to
/// 8 kHz, log with a 2^-24 guard, no normalization. A recording of n samples has 1 + n / 160 frames; the last one lies
/// past the audio and stays zero.
/// </summary>
internal static class MelFrontend
{
    internal const int Mels = 128;
    internal const int SampleRate = 16000;
    internal const int Hop = 160;
    private const int NFft = 512;
    private const int WinLength = 400;
    private const int Bins = (NFft / 2) + 1;
    private const float Preemphasis = 0.97f;
    private const int Block = 64;
    private static readonly float LogGuard = MathF.Pow(2, -24);

    private static readonly float[] Window = BuildWindow();
    private static readonly float[,] Filters = BuildSlaneyFilters();
    private static readonly (int First, int Last)[] FilterSpans = BuildSpans();
    private static readonly (double Cos, double Sin)[] Twiddles = BuildTwiddles();
    private static readonly int[] BitReverse = BuildBitReverse();

    /// <summary>All frames of a recording of <paramref name="samples"/> samples, including the zero frame at the end.</summary>
    internal static int FrameCount(int samples) => 1 + (samples / Hop);

    /// <summary>The frames that carry audio.</summary>
    internal static int ValidFrameCount(int samples) => samples / Hop;

    /// <summary>All frames of the recording, [frames × 128] row-major.</summary>
    internal static float[] Compute(ReadOnlyMemory<float> audio)
    {
        var features = new float[FrameCount(audio.Length) * Mels];
        ComputeFrames(audio, 0, FrameCount(audio.Length), features);
        return features;
    }

    /// <summary>
    /// Writes frames [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>) into
    /// <paramref name="destination"/>, [count × 128] row-major. Frames past the audio are zero. Computing a recording in
    /// pieces gives the same values as computing it at once.
    /// </summary>
    internal static void ComputeFrames(ReadOnlyMemory<float> audio, int first, int count, float[] destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (destination.Length < count * Mels) throw new ArgumentException("The destination is too small.", nameof(destination));
        Array.Clear(destination, 0, count * Mels);
        var valid = ValidFrameCount(audio.Length);
        var last = Math.Min(first + count, valid);
        if (last <= first) return;
        Parallel.For(0, (last - first + Block - 1) / Block, () => (new double[NFft], new double[NFft], new float[Bins]), (block, _, buffers) =>
        {
            var (re, im, power) = buffers;
            var samples = audio.Span;
            var n = samples.Length;
            var end = Math.Min(last, first + ((block + 1) * Block));
            for (var t = first + (block * Block); t < end; t++)
            {
                // Centered frame: the FFT's first point is sample t * hop - n_fft / 2.
                var origin = (t * Hop) - (NFft / 2);
                for (var k = 0; k < NFft; k++)
                {
                    var s = origin + k;
                    var sample = s < 0 || s >= n ? 0f : s == 0 ? samples[0] : samples[s] - (Preemphasis * samples[s - 1]);
                    re[BitReverse[k]] = sample * Window[k];
                    im[BitReverse[k]] = 0;
                }

                Fft(re, im);
                for (var b = 0; b < Bins; b++) power[b] = (float)((re[b] * re[b]) + (im[b] * im[b]));

                var row = (t - first) * Mels;
                for (var m = 0; m < Mels; m++)
                {
                    var (from, to) = FilterSpans[m];
                    var sum = 0f;
                    for (var b = from; b <= to; b++) sum += Filters[m, b] * power[b];
                    destination[row + m] = MathF.Log(sum + LogGuard);
                }
            }

            return buffers;
        }, _ => { });
    }

    private static void Fft(double[] re, double[] im)
    {
        for (var size = 2; size <= NFft; size <<= 1)
        {
            var half = size >> 1;
            var step = NFft / size;
            for (var start = 0; start < NFft; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var (cos, sin) = Twiddles[k * step];
                    var a = start + k;
                    var b = a + half;
                    var tr = (re[b] * cos) + (im[b] * sin);
                    var ti = (im[b] * cos) - (re[b] * sin);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    private static float[] BuildWindow()
    {
        // torch.hann_window(400, periodic=False), centered in the 512-point frame as torch.stft pads it.
        var window = new float[NFft];
        var offset = (NFft - WinLength) / 2;
        for (var i = 0; i < WinLength; i++) window[offset + i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (WinLength - 1))));
        return window;
    }

    private static (double, double)[] BuildTwiddles()
    {
        var twiddles = new (double, double)[NFft / 2];
        for (var k = 0; k < NFft / 2; k++)
        {
            var angle = 2 * Math.PI * k / NFft;
            twiddles[k] = (Math.Cos(angle), Math.Sin(angle));
        }

        return twiddles;
    }

    private static int[] BuildBitReverse()
    {
        var bits = (int)Math.Log2(NFft);
        var map = new int[NFft];
        for (var i = 0; i < NFft; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            map[i] = r;
        }

        return map;
    }

    private const double MelFrequencyStep = 200.0 / 3;
    private const double MinLogHz = 1000;
    private const double MinLogMel = MinLogHz / MelFrequencyStep;
    // Not a static field: the filter bank above is built during type initialization, before later fields are set.
    private static double LogStep => Math.Log(6.4) / 27;

    internal static double HzToMel(double hz) => hz >= MinLogHz ? MinLogMel + (Math.Log(hz / MinLogHz) / LogStep) : hz / MelFrequencyStep;

    internal static double MelToHz(double mel) => mel >= MinLogMel ? MinLogHz * Math.Exp(LogStep * (mel - MinLogMel)) : MelFrequencyStep * mel;

    private static float[,] BuildSlaneyFilters()
    {
        // librosa.filters.mel(sr=16000, n_fft=512, n_mels=128, fmin=0, fmax=8000, htk=False, norm="slaney")
        var fftFreqs = new double[Bins];
        for (var b = 0; b < Bins; b++) fftFreqs[b] = (double)b * SampleRate / NFft;

        var minMel = HzToMel(0);
        var maxMel = HzToMel(SampleRate / 2.0);
        var melF = new double[Mels + 2];
        for (var i = 0; i < Mels + 2; i++) melF[i] = MelToHz(minMel + ((maxMel - minMel) * i / (Mels + 1)));

        var filters = new float[Mels, Bins];
        for (var m = 0; m < Mels; m++)
        {
            var lowerDiff = melF[m + 1] - melF[m];
            var upperDiff = melF[m + 2] - melF[m + 1];
            var norm = 2.0 / (melF[m + 2] - melF[m]);
            for (var b = 0; b < Bins; b++)
            {
                var lower = -(melF[m] - fftFreqs[b]) / lowerDiff;
                var upper = (melF[m + 2] - fftFreqs[b]) / upperDiff;
                filters[m, b] = (float)(Math.Max(0, Math.Min(lower, upper)) * norm);
            }
        }

        return filters;
    }

    private static (int, int)[] BuildSpans()
    {
        var spans = new (int, int)[Mels];
        for (var m = 0; m < Mels; m++)
        {
            int first = -1, last = -1;
            for (var b = 0; b < Bins; b++)
            {
                if (Filters[m, b] == 0) continue;
                if (first < 0) first = b;
                last = b;
            }

            spans[m] = first < 0 ? (0, -1) : (first, last);
        }

        return spans;
    }
}
