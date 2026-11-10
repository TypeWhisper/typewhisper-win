namespace TypeWhisper.DiarizationBenchmarks.Workers;

/// <summary>
/// Log-mel features of Nemotron 3 Diarization, a port of Transformers'
/// NemotronAsrStreamingFeatureExtractor (the same front end as NeMo's AudioToMelSpectrogramPreprocessor
/// at inference): pre-emphasis 0.97, 25 ms symmetric Hann window in a 512-point FFT, 10 ms hop,
/// centered frames with zero padding, power spectrum, 128 Slaney mel bands from 0 to 8 kHz,
/// log with a 2^-24 guard, no normalization. Frames past the audio's length are zeroed.
/// </summary>
internal static class MelFrontend
{
    public const int Mels = 128;
    private const int SampleRate = 16000;
    private const int NFft = 512;
    private const int Hop = 160;
    private const int WinLength = 400;
    private const int Bins = (NFft / 2) + 1;
    private const float Preemphasis = 0.97f;
    private static readonly float LogGuard = MathF.Pow(2, -24);

    private static readonly float[] Window = BuildWindow();
    private static readonly float[,] Filters = BuildSlaneyFilters();
    private static readonly (int First, int Last)[] FilterSpans = BuildSpans();
    private static readonly (double Cos, double Sin)[] Twiddles = BuildTwiddles();
    private static readonly int[] BitReverse = BuildBitReverse();

    /// <summary>Returns [frames × 128] row-major features and the number of valid frames.</summary>
    public static float[] Compute(float[] audio, out int frames, out int validFrames)
    {
        var n = audio.Length;
        var emphasized = new float[n];
        if (n > 0)
        {
            emphasized[0] = audio[0];
        }

        for (var i = 1; i < n; i++)
        {
            emphasized[i] = audio[i] - (Preemphasis * audio[i - 1]);
        }

        var total = 1 + (n / Hop);
        var valid = n / Hop;
        frames = total;
        validFrames = valid;
        var features = new float[total * Mels];
        Parallel.For(0, (total + 255) / 256, () => (new double[NFft], new double[NFft], new float[Bins]), (block, _, buffers) =>
        {
            var (re, im, power) = buffers;
            var end = Math.Min(total, (block + 1) * 256);
            for (var t = block * 256; t < Math.Min(end, valid); t++)
            {
                // Centered frame: sample index of the FFT's first point is t*hop - n_fft/2.
                var origin = (t * Hop) - (NFft / 2);
                for (var k = 0; k < NFft; k++)
                {
                    var s = origin + k;
                    var sample = s >= 0 && s < n ? emphasized[s] : 0f;
                    re[BitReverse[k]] = sample * Window[k];
                    im[BitReverse[k]] = 0;
                }

                Fft(re, im);
                for (var b = 0; b < Bins; b++)
                {
                    power[b] = (float)((re[b] * re[b]) + (im[b] * im[b]));
                }

                var row = t * Mels;
                for (var m = 0; m < Mels; m++)
                {
                    var (first, last) = FilterSpans[m];
                    var sum = 0f;
                    for (var b = first; b <= last; b++)
                    {
                        sum += Filters[m, b] * power[b];
                    }

                    features[row + m] = MathF.Log(sum + LogGuard);
                }
            }

            return buffers;
        }, _ => { });
        return features;
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
        for (var i = 0; i < WinLength; i++)
        {
            window[offset + i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (WinLength - 1))));
        }

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
            for (var b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            map[i] = r;
        }

        return map;
    }

    private static double HzToMel(double hz)
    {
        const double fSp = 200.0 / 3;
        const double minLogHz = 1000;
        var minLogMel = minLogHz / fSp;
        var logStep = Math.Log(6.4) / 27;
        return hz >= minLogHz ? minLogMel + (Math.Log(hz / minLogHz) / logStep) : hz / fSp;
    }

    private static double MelToHz(double mel)
    {
        const double fSp = 200.0 / 3;
        const double minLogHz = 1000;
        var minLogMel = minLogHz / fSp;
        var logStep = Math.Log(6.4) / 27;
        return mel >= minLogMel ? minLogHz * Math.Exp(logStep * (mel - minLogMel)) : fSp * mel;
    }

    private static float[,] BuildSlaneyFilters()
    {
        // librosa.filters.mel(sr=16000, n_fft=512, n_mels=128, fmin=0, fmax=8000, htk=False, norm="slaney")
        var fftFreqs = new double[Bins];
        for (var b = 0; b < Bins; b++)
        {
            fftFreqs[b] = (double)b * SampleRate / NFft;
        }

        var minMel = HzToMel(0);
        var maxMel = HzToMel(SampleRate / 2.0);
        var melF = new double[Mels + 2];
        for (var i = 0; i < Mels + 2; i++)
        {
            melF[i] = MelToHz(minMel + ((maxMel - minMel) * i / (Mels + 1)));
        }

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
                if (Filters[m, b] != 0)
                {
                    if (first < 0)
                    {
                        first = b;
                    }

                    last = b;
                }
            }

            spans[m] = first < 0 ? (0, -1) : (first, last);
        }

        return spans;
    }
}
