using TypeWhisper.Plugin.Diarization.Nemotron;

namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

public sealed class MelFrontendTests
{
    // 8037 samples of a 440 Hz tone plus a slow chirp, as fed to Transformers' NemotronAsrStreamingFeatureExtractor.
    private static float[] Signal() => Enumerable.Range(0, 8037)
        .Select(i => (float)((0.3 * Math.Sin(2 * Math.PI * 440 * i / 16000)) + (0.1 * Math.Sin(2 * Math.PI * (200 + (i * 0.25)) * i / 16000))))
        .ToArray();

    [Theory]
    // Reference values from transformers 5.22 (NemotronAsrStreamingFeatureExtractor of the pinned model revision).
    [InlineData(0, 0, -8.143281936645508)]
    [InlineData(0, 10, -5.602939605712891)]
    [InlineData(1, 5, -9.229955673217773)]
    [InlineData(10, 20, -3.043684720993042)]
    [InlineData(25, 40, -16.49064826965332)]
    [InlineData(30, 64, -16.62253189086914)]
    [InlineData(49, 127, -16.635066986083984)]
    [InlineData(50, 0, 0)]
    public void MatchesTheTransformersFeatureExtractor(int frame, int mel, double expected)
    {
        var features = MelFrontend.Compute(Signal());

        Assert.Equal(51 * MelFrontend.Mels, features.Length);
        Assert.Equal(expected, features[(frame * MelFrontend.Mels) + mel], 3);
    }

    [Fact]
    public void FramesComputedInPiecesEqualTheWholeRecording()
    {
        var audio = Signal();
        var whole = MelFrontend.Compute(audio);
        var pieces = new List<float>();
        foreach (var (first, count) in new[] { (0, 7), (7, 30), (37, 14) })
        {
            var buffer = new float[count * MelFrontend.Mels];
            MelFrontend.ComputeFrames(audio, first, count, buffer);
            pieces.AddRange(buffer);
        }

        Assert.Equal(whole, pieces.ToArray());
    }

    [Fact]
    public void FramesPastTheAudioAreZeroAndSilenceIsTheLogGuard()
    {
        Assert.Equal(1, MelFrontend.FrameCount(0));
        Assert.Equal(0, MelFrontend.ValidFrameCount(159));
        Assert.Equal(2, MelFrontend.FrameCount(160));

        var buffer = new float[10 * MelFrontend.Mels];
        MelFrontend.ComputeFrames(new float[1600], 5, 10, buffer);
        // Frames 5 to 9 hold silence, 10 to 14 lie past the 10 valid frames.
        Assert.All(buffer[..(5 * MelFrontend.Mels)], value => Assert.Equal(MathF.Log(MathF.Pow(2, -24)), value, 4));
        Assert.All(buffer[(5 * MelFrontend.Mels)..], value => Assert.Equal(0f, value));
    }

    [Fact]
    public void SlaneyMelScaleIsLinearBelowOneKilohertzAndLogarithmicAbove()
    {
        Assert.Equal(15, MelFrontend.HzToMel(1000), 9);
        Assert.Equal(3, MelFrontend.HzToMel(200), 9);
        Assert.Equal(4000, MelFrontend.MelToHz(MelFrontend.HzToMel(4000)), 6);
    }
}
