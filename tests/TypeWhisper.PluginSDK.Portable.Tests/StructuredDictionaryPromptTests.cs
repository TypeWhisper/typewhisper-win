using System.Text.Json;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using Xunit;

public sealed class StructuredDictionaryPromptTests
{
    [Fact]
    public void TermsRoundTripWithPunctuationUnicodeAndOrdering()
    {
        string[] terms = ["Washington, D.C.", "Grüße; Öl", "a\nb", "quoted \"name\"", "back\\slash"];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(terms.Concat(["washington, d.c.", " "]));
        Assert.Equal(terms, PluginDictionaryTerms.ParsePrompt(prompt));
        Assert.Equal(string.Join(", ", terms), PluginDictionaryTerms.ToPlainPrompt(prompt));
        Assert.Null(PluginDictionaryTerms.CreateStructuredPrompt([]));
        Assert.Equal("keep; raw, prompt", PluginDictionaryTerms.ToPlainPrompt("keep; raw, prompt"));
    }

    [Fact]
    public void BudgetAppliesToTermsRatherThanJsonEscapingOverhead()
    {
        string[] terms = ["A,B", "Ö"];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt([.. terms, "too long"], new(MaxTerms: 2, MaxTotalChars: 6));
        Assert.Equal(terms, PluginDictionaryTerms.ParsePrompt(prompt));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[123]")]
    [InlineData("[broken")]
    public void MalformedEnvelopeIsRejected(string json) =>
        Assert.Throws<JsonException>(() => PluginDictionaryTerms.ParsePrompt("TypeWhisper.DictionaryTerms/1\n" + json));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothBatchRoutesPreserveTermsOnlyForOptedInPlugins(bool hints)
    {
        foreach (var structured in new[] { false, true })
        {
            var engine = new Mock<ITranscriptionEnginePlugin>();
            engine.SetupGet(e => e.SupportsDictionaryTerms).Returns(true);
            engine.SetupGet(e => e.SupportsStructuredDictionaryTerms).Returns(structured);
            engine.SetupGet(e => e.SupportsLanguageHints).Returns(hints);
            engine.SetupGet(e => e.SupportedLanguages).Returns([]);
            engine.SetupGet(e => e.DictionaryTermsBudget).Returns(new DictionaryTermsBudget(MaxTerms: 2));
            string[] terms = ["Washington, D.C.", "Grüße"];
            var prompt = structured ? PluginDictionaryTerms.CreateStructuredPrompt(terms) : "Washington, D.C., Grüße";
            byte[] audio = [1]; string[] languages = ["de"];
            var result = new PluginTranscriptionResult("test", "de", 1, null);
            engine.Setup(e => e.TranscribeAsync(audio, null, false, prompt, default)).ReturnsAsync(result);
            engine.Setup(e => e.TranscribeWithLanguageHintsAsync(audio, languages, false, prompt, default)).ReturnsAsync(result);
            Assert.Same(result, await LanguageHintTranscription.DecodeAsync(engine.Object, ReadOnlyMemory<float>.Empty,
                () => audio, null, languages, false, default, [.. terms, "clipped"]));
        }
    }

    [Fact]
    public async Task StreamingUsesTheSameStructuredTermsForEligibilityAndConnection()
    {
        var engine = new Mock<ITranscriptionEnginePlugin>();
        engine.SetupGet(e => e.SupportsStreaming).Returns(true);
        engine.SetupGet(e => e.SupportsStreamingCompletion).Returns(true);
        engine.SetupGet(e => e.SupportsDictionaryTerms).Returns(true);
        engine.SetupGet(e => e.SupportsStructuredDictionaryTerms).Returns(true);
        string[] terms = ["Washington, D.C.", "Öl"];
        var prompt = PluginDictionaryTerms.CreateStructuredPrompt(terms);
        engine.Setup(e => e.SupportsStreamingForPrompt(prompt)).Returns(true);
        var session = new Mock<IStreamingSession>();
        session.Setup(s => s.FinalizeAsync(It.IsAny<CancellationToken>())).Callback(() =>
            session.Raise(s => s.TranscriptReceived += null, new StreamingTranscriptEvent("done", true))).Returns(Task.CompletedTask);
        engine.Setup(e => e.StartStreamingWithLanguageHintsAndPromptAsync(It.IsAny<IReadOnlyList<string>>(), prompt,
            It.IsAny<CancellationToken>())).ReturnsAsync(session.Object);
        var failed = false;
        await using var stream = new StreamingDictation((use, ct) => use(engine.Object, ct), ["de"], _ => {}, () => failed = true, default, terms);
        Assert.Equal("done", await stream.FinishAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(failed);
        engine.Verify(e => e.SupportsStreamingForPrompt(prompt), Times.Once());
        engine.Verify(e => e.StartStreamingWithLanguageHintsAndPromptAsync(It.IsAny<IReadOnlyList<string>>(), prompt,
            It.IsAny<CancellationToken>()), Times.Once());
    }
}
