using System.Numerics;

namespace TypeWhisper.Plugin.ParakeetCtc;

// NeMo configuration used by sherpa-onnx: 16 kHz, 25 ms periodic Hann,
// 10 ms shift, reflected edges, preemphasis .97, Slaney mel, per-feature CMVN.
// See THIRD-PARTY-NOTICES.md for the reference implementations.
internal static class NemoFeatures
{
    private const int Window = 400, Hop = 160, FftSize = 512, Bins = 80;
    private static readonly double[] Hann = Enumerable.Range(0, Window).Select(i => .5 - .5 * Math.Cos(2 * Math.PI * i / Window)).ToArray();
    private static readonly (int Bin, double Weight)[][] Filters = MakeFilters();

    internal static (float[] Values, int Frames) Extract(ReadOnlySpan<float> audio, CancellationToken cancellation)
    {
        if (audio.Length < Window) throw new ArgumentException("Audio is too short for CTC features.");
        var frames = (audio.Length + Hop / 2) / Hop;
        var values = new float[frames * Bins]; // [mel, time]
        var fft = new Complex[FftSize];
        var samples = new double[Window];
        for (var frame = 0; frame < frames; frame++)
        {
            cancellation.ThrowIfCancellationRequested();
            Array.Clear(fft);
            var start = frame * Hop + Hop / 2 - Window / 2;
            for (var i = 0; i < Window; i++)
            {
                var index = start + i;
                while (index < 0 || index >= audio.Length)
                    index = index < 0 ? -index - 1 : 2 * audio.Length - 1 - index;
                if (!float.IsFinite(audio[index])) throw new ArgumentException("Non-finite PCM sample.");
                samples[i] = audio[index];
            }
            for (var i = Window - 1; i >= 0; i--)
                fft[i] = (samples[i] - .97 * samples[Math.Max(0, i - 1)]) * Hann[i];
            Transform(fft);
            for (var mel = 0; mel < Bins; mel++)
            {
                double energy = 0;
                foreach (var (bin, weight) in Filters[mel])
                    energy += (fft[bin].Real * fft[bin].Real + fft[bin].Imaginary * fft[bin].Imaginary) * weight;
                values[mel * frames + frame] = (float)Math.Log(Math.Max(energy, 1.1920928955078125e-7));
            }
        }
        for (var mel = 0; mel < Bins; mel++)
        {
            var offset = mel * frames;
            double mean = 0;
            for (var t = 0; t < frames; t++) mean += values[offset + t];
            mean /= frames;
            double variance = 0;
            for (var t = 0; t < frames; t++) variance += Math.Pow(values[offset + t] - mean, 2);
            var std = Math.Sqrt(variance / Math.Max(1, frames - 1)) + 1e-5;
            for (var t = 0; t < frames; t++) values[offset + t] = (float)((values[offset + t] - mean) / std);
        }
        return (values, frames);
    }

    private static (int, double)[][] MakeFilters()
    {
        static double Mel(double hz) => hz < 1000 ? hz / (200d / 3) : 15 + Math.Log(hz / 1000) / (Math.Log(6.4) / 27);
        static double Hertz(double mel) => mel < 15 ? mel * (200d / 3) : 1000 * Math.Exp((mel - 15) * Math.Log(6.4) / 27);
        var edges = Enumerable.Range(0, Bins + 2).Select(i => Hertz(Mel(8000) * i / (Bins + 1))).ToArray();
        return Enumerable.Range(0, Bins).Select(m => Enumerable.Range(0, FftSize / 2 + 1)
            .Select(b => (b, Math.Max(0, Math.Min((b * 16000d / FftSize - edges[m]) / (edges[m + 1] - edges[m]),
                (edges[m + 2] - b * 16000d / FftSize) / (edges[m + 2] - edges[m + 1]))) * 2 / (edges[m + 2] - edges[m])))
            .Where(p => p.Item2 > 0).ToArray()).ToArray();
    }

    private static void Transform(Complex[] data)
    {
        for (int i = 1, j = 0; i < data.Length; i++)
        {
            var bit = data.Length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (var length = 2; length <= data.Length; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var i = 0; i < data.Length; i += length)
            {
                var w = Complex.One;
                for (var j = 0; j < length / 2; j++, w *= step)
                {
                    var a = data[i + j]; var b = data[i + j + length / 2] * w;
                    data[i + j] = a + b; data[i + j + length / 2] = a - b;
                }
            }
        }
    }
}
