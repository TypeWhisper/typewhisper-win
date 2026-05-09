namespace TypeWhisper.Plugin.Qwen3Stt;

internal static class Qwen3AudioPreprocessor
{
    private const int SampleRate = 16000;
    private const int FeatureBins = 128;
    private const int FftSize = 400;
    private const int HopLength = 160;
    private const int MaxFrequency = 8000;

    private static readonly float[] HannWindow = BuildHannWindow();
    private static readonly float[,] MelFilters = BuildMelFilters();
    private static readonly double[,] CosTable = BuildTrigTable(Math.Cos);
    private static readonly double[,] SinTable = BuildTrigTable(Math.Sin);

    public static (float[] Features, int Frames) ComputeLogMel(float[] samples)
    {
        if (samples.Length == 0)
            samples = new float[SampleRate / 2];

        var frameCount = Math.Max(1, 1 + Math.Max(0, samples.Length - FftSize) / HopLength);
        var features = new float[FeatureBins * frameCount];
        var power = new double[FftSize / 2 + 1];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * HopLength;
            ComputePowerSpectrum(samples, start, power);

            for (var mel = 0; mel < FeatureBins; mel++)
            {
                var energy = 0.0;
                for (var bin = 0; bin < power.Length; bin++)
                    energy += power[bin] * MelFilters[mel, bin];

                var log = Math.Log10(Math.Max(energy, 1e-10));
                features[mel * frameCount + frame] = (float)log;
            }
        }

        NormalizeLikeWhisper(features);
        return (features, frameCount);
    }

    public static int FeatureOutputLength(int frames)
    {
        var leave = frames % 100;
        var featLengths = (leave - 1) / 2 + 1;
        return ((featLengths - 1) / 2 + 1 - 1) / 2 + 1 + frames / 100 * 13;
    }

    private static void ComputePowerSpectrum(float[] samples, int start, double[] output)
    {
        Array.Clear(output);
        for (var k = 0; k < output.Length; k++)
        {
            var real = 0.0;
            var imag = 0.0;
            for (var n = 0; n < FftSize; n++)
            {
                var index = start + n;
                var sample = index < samples.Length ? samples[index] : 0f;
                var windowed = sample * HannWindow[n];
                real += windowed * CosTable[k, n];
                imag -= windowed * SinTable[k, n];
            }

            output[k] = real * real + imag * imag;
        }
    }

    private static void NormalizeLikeWhisper(float[] features)
    {
        var max = features.Max();
        var floor = max - 8.0f;
        for (var i = 0; i < features.Length; i++)
        {
            var value = Math.Max(features[i], floor);
            features[i] = (value + 4.0f) / 4.0f;
        }
    }

    private static float[] BuildHannWindow()
    {
        var window = new float[FftSize];
        for (var i = 0; i < window.Length; i++)
            window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize));
        return window;
    }

    private static float[,] BuildMelFilters()
    {
        var filters = new float[FeatureBins, FftSize / 2 + 1];
        var melMin = HertzToMel(0);
        var melMax = HertzToMel(MaxFrequency);
        var points = new double[FeatureBins + 2];
        for (var i = 0; i < points.Length; i++)
            points[i] = MelToHertz(melMin + (melMax - melMin) * i / (FeatureBins + 1));

        var fftFrequencies = Enumerable.Range(0, FftSize / 2 + 1)
            .Select(i => (double)i * SampleRate / FftSize)
            .ToArray();

        for (var mel = 0; mel < FeatureBins; mel++)
        {
            var left = points[mel];
            var center = points[mel + 1];
            var right = points[mel + 2];
            for (var bin = 0; bin < fftFrequencies.Length; bin++)
            {
                var freq = fftFrequencies[bin];
                var lower = (freq - left) / Math.Max(center - left, double.Epsilon);
                var upper = (right - freq) / Math.Max(right - center, double.Epsilon);
                var areaNorm = 2.0 / Math.Max(right - left, double.Epsilon);
                filters[mel, bin] = (float)(Math.Max(0, Math.Min(lower, upper)) * areaNorm);
            }
        }

        return filters;
    }

    private static double HertzToMel(double hertz) =>
        hertz < 1000.0
            ? hertz / (200.0 / 3.0)
            : 15.0 + Math.Log(hertz / 1000.0) / (Math.Log(6.4) / 27.0);

    private static double MelToHertz(double mel) =>
        mel < 15.0
            ? mel * (200.0 / 3.0)
            : 1000.0 * Math.Exp((mel - 15.0) * (Math.Log(6.4) / 27.0));

    private static double[,] BuildTrigTable(Func<double, double> trig)
    {
        var table = new double[FftSize / 2 + 1, FftSize];
        for (var k = 0; k < FftSize / 2 + 1; k++)
        {
            for (var n = 0; n < FftSize; n++)
                table[k, n] = trig(2.0 * Math.PI * k * n / FftSize);
        }
        return table;
    }
}
