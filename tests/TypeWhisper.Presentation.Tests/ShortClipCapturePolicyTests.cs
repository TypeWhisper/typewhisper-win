using TypeWhisper.Presentation;
using Xunit;

public sealed class ShortClipCapturePolicyTests
{
    [Theory]
    [InlineData(0, 1, false, false, ShortClipCaptureDecision.TooShort)]
    [InlineData(0.039, 1, true, true, ShortClipCaptureDecision.TooShort)]
    [InlineData(0.04, 0.003, false, false, ShortClipCaptureDecision.Transcribe)]
    [InlineData(0.12, 0.0029, false, false, ShortClipCaptureDecision.NoSpeech)]
    [InlineData(0.12, 0.0029, false, true, ShortClipCaptureDecision.Transcribe)]
    [InlineData(0.12, 0.0029, true, false, ShortClipCaptureDecision.Transcribe)]
    [InlineData(0.999, 0.003, false, false, ShortClipCaptureDecision.Transcribe)]
    [InlineData(1, 0.003, false, false, ShortClipCaptureDecision.NoSpeech)]
    [InlineData(1.2, 0.0059, false, false, ShortClipCaptureDecision.NoSpeech)]
    [InlineData(1.2, 0.006, false, false, ShortClipCaptureDecision.Transcribe)]
    [InlineData(1.2, 0.0059, false, true, ShortClipCaptureDecision.Transcribe)]
    public void MatchesExistingWindowsThresholds(double duration, float peak, bool confirmed, bool enabled,
        ShortClipCaptureDecision expected) =>
        Assert.Equal(expected, ShortClipCapturePolicy.Classify(duration, peak, confirmed, enabled));

    [Theory]
    [InlineData(640, 12000)]
    [InlineData(1280, 12000)]
    [InlineData(11999, 12000)]
    [InlineData(12000, 16800)]
    [InlineData(19200, 24000)]
    public void PaddingPreservesEveryOriginalSampleAndCtcIndex(int count, int paddedCount)
    {
        var original = Enumerable.Range(0, count).Select(index => (float)index / count).ToArray();
        var snapshot = original.ToArray();
        var duration = original.Length / 16000.0;
        var padded = ShortClipCapturePolicy.PadForFinalDecode(original);
        Assert.Equal(paddedCount, padded.Length);
        Assert.Equal(snapshot, original);
        Assert.Equal(snapshot, padded.Take(count).ToArray());
        Assert.All(padded.Skip(count), sample => Assert.Equal(0f, sample));
        Assert.Equal(duration, original.Length / 16000.0);
        Assert.NotSame(original, padded);
    }

    [Fact]
    public void OlderPreferencesKeepQuietRecognitionDisabled()
    {
        var path = Path.Combine(Path.GetTempPath(), "typewhisper-quiet-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """
                {"TranscriptionNumberNormalizationEnabled":true,"ShortUtterancePunctuationEnabled":true,
                 "EnglishOutputVariant":"AsTranscribed","GermanOutputVariant":"AsTranscribed"}
                """);
            var store = new DictationTextPreferencesStore(path);
            Assert.Null(store.Error);
            Assert.False(store.Current.TranscribeShortQuietClipsAggressively);
        }
        finally { File.Delete(path); }
    }
}
