using TypeWhisper.Plugin.Diarization.Nemotron;

namespace TypeWhisper.Plugin.Diarization.Nemotron.Tests;

public sealed class NemotronStreamingDiarizerTests
{
    private const int Hidden = NemotronStreamingDiarizer.Hidden;
    private const int Speakers = NemotronStreamingDiarizer.Speakers;
    private static readonly NemotronProfile Small = new("small", 4, 1, 6, 16, 4);

    // A stand-in for the graph that depends only on the valid inputs: cached frames speak through their embeddings,
    // new frames through their features. Padded positions therefore cannot change the valid outputs.
    private sealed class FakeStep : INemotronStep
    {
        internal readonly List<(int MelFrames, int CachedFrames, int MaskLength)> Calls = [];

        public NemotronStepOutput Run(float[] features, int melFrames, float[] cached, int cachedFrames, long[] mask, CancellationToken cancellationToken)
        {
            Calls.Add((melFrames, cachedFrames, mask.Length));
            Assert.Equal(melFrames * MelFrontend.Mels, features.Length);
            Assert.Equal(cachedFrames * Hidden, cached.Length);
            var step = (melFrames + 7) / 8;
            Assert.Equal(cachedFrames + step, mask.Length);
            var total = cachedFrames + step;
            var logits = new float[total * 8 * Speakers];
            for (var f = 0; f < total; f++)
            {
                for (var k = 0; k < 8; k++)
                {
                    for (var s = 0; s < Speakers; s++)
                    {
                        logits[(((f * 8) + k) * Speakers) + s] = f < cachedFrames
                            ? cached[(f * Hidden) + s] * 10
                            : Feature(features, ((f - cachedFrames) * 8) + k, melFrames, s);
                    }
                }
            }

            var embeds = new float[step * Hidden];
            for (var j = 0; j < step; j++)
                for (var s = 0; s < Speakers; s++) embeds[(j * Hidden) + s] = Feature(features, j * 8, melFrames, s) / 10;
            return new(logits, embeds, Enumerable.Repeat(0.25f, Hidden).ToArray());
        }

        // Speaker s follows the energy of mel band 16 s. Mel frames past the input count as zero, as the encoder pads them.
        private static float Feature(float[] features, int melFrame, int melFrames, int speaker) =>
            ((melFrame < melFrames ? features[(melFrame * MelFrontend.Mels) + (speaker * 16)] : 0) + 6) / 2;
    }

    // Two "voices" taking turns every 1.5 s: a low and a high tone.
    private static float[] Conversation(double seconds) => Enumerable.Range(0, (int)(seconds * 16000))
        .Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * ((i / 24000) % 2 == 0 ? 300 : 2500) * i / 16000)))
        .ToArray();

    [Theory]
    [InlineData(0.5)]
    [InlineData(3.0)]
    [InlineData(12.37)]
    public void ReturnsOneProbabilityRowPerTenMillisecondFrame(double seconds)
    {
        var audio = Conversation(seconds);
        var probabilities = new NemotronStreamingDiarizer(new FakeStep(), NemotronProfile.Fast32).Run(audio, null, default);

        Assert.Equal(MelFrontend.FrameCount(audio.Length) * Speakers, probabilities.Length);
        Assert.All(probabilities, p => Assert.InRange(p, 0f, 1f));
    }

    [Fact]
    public void FifoMovesToTheSpeakerCacheInUpdatePeriods()
    {
        var step = new FakeStep();
        var diarizer = new NemotronStreamingDiarizer(step, Small);

        // 63 * 160 samples give 64 mel frames, 8 encoder frames: two chunks of 4.
        diarizer.Run(new float[63 * 160], null, default);

        // The first chunk fills the FIFO (4 <= 6); the second overflows it, so one update period moves to the cache.
        Assert.Equal([0, 4], step.Calls.Select(call => call.CachedFrames));
        Assert.Equal((4, 4, false), diarizer.LastState);
    }

    [Fact]
    public void FullSpeakerCacheIsCompressedToItsLength()
    {
        var step = new FakeStep();
        var diarizer = new NemotronStreamingDiarizer(step, Small);

        diarizer.Run(Conversation(8), null, default);

        var (cache, fifo, compressed) = diarizer.LastState;
        Assert.Equal(16, cache);
        Assert.True(compressed);
        Assert.InRange(fifo, 0, 6);
        // Never more cached frames than the cache plus the FIFO.
        Assert.All(step.Calls, call => Assert.InRange(call.CachedFrames, 0, 16 + 6));
    }

    [Theory]
    [InlineData("fast32")]
    [InlineData("small")]
    public void FixedShapesGiveTheSameProbabilitiesAsFreeShapes(string profileId)
    {
        var profile = NemotronProfile.Find(profileId) ?? Small;
        var audio = Conversation(profile == Small ? 9.3 : 41.3);
        var free = new NemotronStreamingDiarizer(new FakeStep(), profile).Run(audio, null, default);
        var fixedStep = new FakeStep();
        var padded = new NemotronStreamingDiarizer(fixedStep, profile, fixedShapes: true).Run(audio, null, default);

        Assert.Equal(free, padded);
        // Every padded step has the same shape.
        Assert.Single(fixedStep.Calls.Distinct());
        Assert.Equal(((profile.ChunkLength + profile.RightContext) * 8, profile.SpeakerCacheLength + profile.FifoLength),
            (fixedStep.Calls[0].MelFrames, fixedStep.Calls[0].CachedFrames));
    }

    [Fact]
    public void ReportsProgressUpToOneAndStopsWhenCancelled()
    {
        var reports = new List<double>();
        new NemotronStreamingDiarizer(new FakeStep(), NemotronProfile.Fast32).Run(Conversation(10), new Recorder(reports), default);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.Order(), reports);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new NemotronStreamingDiarizer(new FakeStep(), NemotronProfile.Fast32).Run(Conversation(10), null, cancelled.Token));
    }

    [Fact]
    public void ProfilesMatchTheModelCardAndFluidAudio()
    {
        Assert.Equal(new NemotronProfile("fast32", 32, 4, 40, 264, 40), NemotronProfile.Fast32);
        Assert.Equal(new NemotronProfile("offline", 340, 40, 40, 264, 300), NemotronProfile.Offline);
        Assert.Null(NemotronProfile.Find("other"));
    }

    private sealed class Recorder(List<double> reports) : IProgress<double>
    {
        public void Report(double value) => reports.Add(value);
    }
}
