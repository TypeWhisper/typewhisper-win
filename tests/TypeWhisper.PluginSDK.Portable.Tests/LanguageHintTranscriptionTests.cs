using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class LanguageHintTranscriptionTests
{
    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "fr", false)]
    public async Task CapabilityAndExplicitLanguageDetermineInvocation(bool capability, string? language, bool hints)
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsLanguageHints).Returns(capability);
        engine.SetupGet(e => e.SupportedLanguages).Returns(["de", "en", "fr"]);
        var response = new PluginTranscriptionResult("Hello", "en", 1, 0.2f);
        string[] preferred = ["de", "en"];
        byte[] wav = [1, 2];
        engine.Setup(e => e.TranscribeWithLanguageHintsAsync(wav, preferred, false, null, default)).ReturnsAsync(response);
        engine.Setup(e => e.TranscribeAsync(wav, language, false, null, default)).ReturnsAsync(response);
        var actual = await LanguageHintTranscription.DecodeAsync(engine.Object, new float[1], () => wav, language, preferred, false, default);
        Assert.Same(response, actual);
        engine.Verify(e => e.TranscribeWithLanguageHintsAsync(wav, preferred, false, null, default), hints ? Times.Once() : Times.Never());
        engine.Verify(e => e.TranscribeAsync(wav, language, false, null, default), hints ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task UnsupportedPcmKeepsOriginalSamplesAndDoesNotEncodeOrForceFirstHint()
    {
        var engine = new Mock<IPcmTranscriptionEnginePlugin>();
        float[] samples = [0.123456f];
        var result = new PluginTranscriptionResult("Hallo", "de", 1, null);
        engine.Setup(e => e.TranscribePcmAsync(samples, null, false, default)).ReturnsAsync(result);
        Assert.Same(result, await LanguageHintTranscription.DecodeAsync(engine.Object, samples,
            () => throw new InvalidOperationException("Must not encode PCM"), null, ["en", "de"], false, default));
    }

    [Fact]
    public async Task CapablePcmUsesExplicitWavHintsContract()
    {
        var engine = new Mock<IPcmTranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsLanguageHints).Returns(true);
        engine.SetupGet(e => e.SupportedLanguages).Returns([]);
        string[] hints = ["de", "en"]; byte[] wav = [1];
        var result = new PluginTranscriptionResult("Hallo", "de", 1, null);
        engine.Setup(e => e.TranscribeWithLanguageHintsAsync(wav, hints, false, null, default)).ReturnsAsync(result);
        Assert.Same(result, await LanguageHintTranscription.DecodeAsync(engine.Object, new float[1], () => wav, null, hints, false, default));
        engine.Verify(e => e.TranscribePcmAsync(It.IsAny<ReadOnlyMemory<float>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task UnsupportedHintIsRejectedBeforeAudioEncoding()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsLanguageHints).Returns(true);
        engine.SetupGet(e => e.SupportedLanguages).Returns(["en"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LanguageHintTranscription.DecodeAsync(engine.Object, new float[1],
            () => throw new Exception("Must not encode"), null, ["de"], false, default));
    }
}
