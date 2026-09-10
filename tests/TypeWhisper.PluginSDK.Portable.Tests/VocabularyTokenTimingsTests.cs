using TypeWhisper.PluginSDK.Helpers;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class VocabularyTokenTimingsTests
{
    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(1e-20f)]
    public void PositiveDurationThatRoundsToZeroUsesNextDistinctFrame(float duration)
    {
        // Observed start/end: 0.6399999856948853. A positive float duration
        // can still disappear when added to this timestamp as a double.
        var result = TranscriptionTokenTimings.Create(["Ich", "Type", "vis", "per"],
            [.32f, .64f, .64f, .8f], [.1f, duration, 0, 0], 3.09);
        Assert.Equal(4, result.Length);
        Assert.All(result, token => Assert.True(token.EndSeconds > token.StartSeconds));
        Assert.Equal((double).8f, result[1].EndSeconds);
    }

    [Fact]
    public void RoundedFinalDurationUsesAudioBoundary()
    {
        var result = TranscriptionTokenTimings.Create(["end"], [.64f], [float.Epsilon], 3.09);
        Assert.Equal(3.09, Assert.Single(result).EndSeconds);
    }

    [Fact]
    public void SharedFramePreservesEverySubword()
    {
        var result = TranscriptionTokenTimings.Create(["Type", "vis", "per", " every"], [0, 0, 0, .8f], [0, 0, 0, .1f], 1);
        Assert.Equal(4, result.Length);
        Assert.Equal("Typevisper", string.Concat(result.Take(3).Select(t => t.Text)));
        Assert.All(result.Take(3), t => Assert.Equal((double).8f, t.EndSeconds));
    }

    [Fact]
    public void FinalSharedFrameUsesAudioEnd()
    {
        var result = TranscriptionTokenTimings.Create(["word", "."], [.5f, .5f], [], 1);
        Assert.Equal(2, result.Length);
        Assert.All(result, t => Assert.Equal(1, t.EndSeconds));
    }

    [Fact]
    public void InvalidTimesFailClosed()
    {
        Assert.Empty(TranscriptionTokenTimings.Create(["a"], [float.NaN], [], 1));
        Assert.Empty(TranscriptionTokenTimings.Create(["a", "b"], [.5f, .1f], [], 1));
    }
}
