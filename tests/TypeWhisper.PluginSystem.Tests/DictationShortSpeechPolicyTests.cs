using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class DictationShortSpeechPolicyTests
{
    [Fact]
    public void EmptyBuffer_IsDiscardedAsTooShort()
    {
        Assert.Equal(
            ShortSpeechDecision.DiscardTooShort,
            DictationShortSpeechPolicy.Classify(0, peakLevel: 0, hasConfirmedText: false));
    }

    [Fact]
    public void ThirtyMsHighPeak_IsStillTooShort()
    {
        Assert.Equal(
            ShortSpeechDecision.DiscardTooShort,
            DictationShortSpeechPolicy.Classify(0.03, peakLevel: 0.2f, hasConfirmedText: false));
    }

    [Fact]
    public void ThirtyMsConfirmedText_IsStillTooShort()
    {
        Assert.Equal(
            ShortSpeechDecision.DiscardTooShort,
            DictationShortSpeechPolicy.Classify(0.03, peakLevel: 0.2f, hasConfirmedText: true));
    }

    [Fact]
    public void ShortClipAboveQuietThreshold_TranscribesAndPadsToMinimumDuration()
    {
        Assert.Equal(
            ShortSpeechDecision.Transcribe,
            DictationShortSpeechPolicy.Classify(0.08, peakLevel: 0.008f, hasConfirmedText: false));

        var paddedSamples = DictationShortSpeechPolicy.PadSamplesForFinalTranscription(
            MakeSamples(0.08),
            rawDuration: 0.08);

        Assert.Equal(12000, paddedSamples.Length);
        Assert.Equal(0.75, paddedSamples.Length / 16000.0, precision: 4);
    }

    [Fact]
    public void ShortVeryQuietClip_IsNoSpeechByDefault()
    {
        Assert.Equal(
            ShortSpeechDecision.DiscardNoSpeech,
            DictationShortSpeechPolicy.Classify(0.12, peakLevel: 0.0029f, hasConfirmedText: false));
    }

    [Fact]
    public void ShortQuietClip_TranscribesWhenAggressivePolicyEnabled()
    {
        Assert.Equal(
            ShortSpeechDecision.Transcribe,
            DictationShortSpeechPolicy.Classify(
                0.12,
                peakLevel: 0.0029f,
                hasConfirmedText: false,
                transcribeShortQuietClipsAggressively: true));
    }

    [Fact]
    public void ShortQuietClip_WithConfirmedText_Transcribes()
    {
        Assert.Equal(
            ShortSpeechDecision.Transcribe,
            DictationShortSpeechPolicy.Classify(0.12, peakLevel: 0.0029f, hasConfirmedText: true));
    }

    [Fact]
    public void LongVeryQuietClip_IsNoSpeechByDefault()
    {
        Assert.Equal(
            ShortSpeechDecision.DiscardNoSpeech,
            DictationShortSpeechPolicy.Classify(1.2, peakLevel: 0.0059f, hasConfirmedText: false));
    }

    [Fact]
    public void LongQuietClip_TranscribesWhenAggressivePolicyEnabled()
    {
        Assert.Equal(
            ShortSpeechDecision.Transcribe,
            DictationShortSpeechPolicy.Classify(
                1.2,
                peakLevel: 0.0059f,
                hasConfirmedText: false,
                transcribeShortQuietClipsAggressively: true));
    }

    [Fact]
    public void LongClipAboveQuietThreshold_TranscribesAndGetsTailPadding()
    {
        Assert.Equal(
            ShortSpeechDecision.Transcribe,
            DictationShortSpeechPolicy.Classify(1.2, peakLevel: 0.0061f, hasConfirmedText: false));

        var paddedSamples = DictationShortSpeechPolicy.PadSamplesForFinalTranscription(
            MakeSamples(1.2),
            rawDuration: 1.2);

        Assert.Equal(24000, paddedSamples.Length);
        Assert.Equal(1.5, paddedSamples.Length / 16000.0, precision: 4);
    }

    private static float[] MakeSamples(double durationSeconds) =>
        Enumerable.Repeat(0.1f, (int)(durationSeconds * 16000)).ToArray();
}

public class DictationFinalTextPolicyTests
{
    [Fact]
    public void SelectRawText_FinalTextWinsOverStalePreview()
    {
        var result = DictationFinalTextPolicy.SelectRawText("Das ist der komplette Satz mit Ende.");

        Assert.Equal("Das ist der komplette Satz mit Ende.", result);
    }

    [Fact]
    public void SelectRawText_DoesNotUsePreviewWhenFinalTextIsEmpty()
    {
        var result = DictationFinalTextPolicy.SelectRawText("");

        Assert.Equal("", result);
    }

    [Fact]
    public void SelectRawText_CollapsesIssue90AdjacentRepeatedPhrase()
    {
        const string rawText =
            "It would be really cool if the amount of time that the preview bubble remains after you paste could be setable. " +
            "I am mindful of settings proliferation. And the current preview time. " +
            "is probably close to being right, if not a little bit on the wrong. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "But given that this is such a core part of the user interaction.";
        const string expected =
            "It would be really cool if the amount of time that the preview bubble remains after you paste could be setable. " +
            "I am mindful of settings proliferation. And the current preview time. " +
            "is probably close to being right, if not a little bit on the wrong long side right now. " +
            "But given that this is such a core part of the user interaction.";

        var result = DictationFinalTextPolicy.SelectRawText(rawText);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SelectRawText_CollapsesIssue108ShortAdjacentRepeatedPhrase()
    {
        const string rawText =
            "Now go back to the appearance screen. and set the and set the preview text size to the maximum. " +
            "Dictate more text and note that the size of the preview bubble text is unchanged.";
        const string expected =
            "Now go back to the appearance screen. and set the preview text size to the maximum. " +
            "Dictate more text and note that the size of the preview bubble text is unchanged.";

        var result = DictationFinalTextPolicy.SelectRawText(rawText);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void SelectRawText_CollapsesExactAdjacentRepeatedPhrase()
    {
        var result = DictationFinalTextPolicy.SelectRawText(
            "Please send the updated draft tomorrow morning. Please send the updated draft tomorrow morning. Thanks.");

        Assert.Equal("Please send the updated draft tomorrow morning. Thanks.", result);
    }

    [Fact]
    public void SelectRawText_PreservesShortIntentionalRepeats()
    {
        var result = DictationFinalTextPolicy.SelectRawText("Yes yes, that's right.");

        Assert.Equal("Yes yes, that's right.", result);
    }

    [Fact]
    public void SelectRawText_WhitespaceOnlyReturnsEmptyText()
    {
        var result = DictationFinalTextPolicy.SelectRawText("   ");

        Assert.Equal("", result);
    }

    [Fact]
    public void SelectRawText_TrustedLiveTextWinsWithoutFinalText()
    {
        var result = DictationFinalTextPolicy.SelectRawText(null, "  confirmed live transcript  ");

        Assert.Equal("confirmed live transcript", result);
    }

    [Fact]
    public void SelectRawText_CollapsesTrustedLiveTextRepeatedPhrase()
    {
        var result = DictationFinalTextPolicy.SelectRawText(
            "fallback text",
            "Please send the updated draft tomorrow morning. Please send the updated draft tomorrow morning.");

        Assert.Equal("Please send the updated draft tomorrow morning.", result);
    }

    [Fact]
    public void SelectRawText_BlankTrustedLiveTextFallsBackToFinalText()
    {
        var result = DictationFinalTextPolicy.SelectRawText("final transcript", "   ");

        Assert.Equal("final transcript", result);
    }

    [Fact]
    public void SelectTrustedLiveText_ReturnsNullForBlankText()
    {
        Assert.Null(DictationFinalTextPolicy.SelectTrustedLiveText("   "));
    }

    [Fact]
    public void ShouldRejectAsNoSpeech_RejectsEmptyFinalTextEvenWhenPreviewExists()
    {
        var reject = DictationFinalTextPolicy.ShouldRejectAsNoSpeech(
            "",
            noSpeechProbability: null,
            hasPreviewText: true,
            transcribeShortQuietClipsAggressively: false);

        Assert.True(reject);
    }

    [Fact]
    public void ShouldRejectAsNoSpeech_RejectsHighNoSpeechFinalTextWithoutPreview()
    {
        var reject = DictationFinalTextPolicy.ShouldRejectAsNoSpeech(
            "Thank you.",
            noSpeechProbability: 0.95f,
            hasPreviewText: false,
            transcribeShortQuietClipsAggressively: false);

        Assert.True(reject);
    }

    [Fact]
    public void ShouldRejectAsNoSpeech_AllowsHighNoSpeechFinalTextWhenPreviewConfirmsSpeech()
    {
        var reject = DictationFinalTextPolicy.ShouldRejectAsNoSpeech(
            "This is the final transcript.",
            noSpeechProbability: 0.95f,
            hasPreviewText: true,
            transcribeShortQuietClipsAggressively: false);

        Assert.False(reject);
    }
}
