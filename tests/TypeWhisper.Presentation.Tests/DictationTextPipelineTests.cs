using TypeWhisper.Core.Models;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationTextPipelineTests
{
    [Fact]
    public async Task SharedPipelineOrdersBuiltInsSnippetsBoostingCorrectionsAndRegionalSpelling()
    {
        var observed = new List<string>();
        var result = await DictationTextPipeline.ProcessAsync("two.", new()
        {
            ShortUtterancePunctuationEnabled = false,
            EnglishOutputVariant = EnglishOutputVariant.UnitedKingdom
        }, "en", expandSnippets: async (text, ct) =>
        {
            await Task.Yield();
            observed.Add("snippet:" + text);
            return "snippet colur";
        }, boostVocabulary: text =>
        {
            observed.Add("boost:" + text);
            return text.Replace("colur", "color");
        }, correctDictionary: text =>
        {
            observed.Add("dictionary:" + text);
            return text.Replace("snippet", "favorite");
        });
        Assert.Equal(new[] { "snippet:2", "boost:snippet colur", "dictionary:snippet color" }, observed);
        Assert.Equal("favourite colour", result.Text);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(true, true, "2.")]
    [InlineData(false, true, "two.")]
    [InlineData(true, false, "2")]
    [InlineData(false, false, "two")]
    public async Task NumbersAndShortPunctuationCanBeControlledIndependently(bool numbers, bool punctuation, string expected)
    {
        var result = await DictationTextPipeline.ProcessAsync("two.", new()
        {
            TranscriptionNumberNormalizationEnabled = numbers,
            ShortUtterancePunctuationEnabled = punctuation
        }, "en");
        Assert.Equal(expected, result.Text);
    }

    [Theory]
    [InlineData("de", null, "Strasse")]
    [InlineData("auto", "de", "Strasse")]
    [InlineData("en", null, "Straße")]
    public async Task SwissSpellingUsesTranscriptLanguageAfterDictionary(string configured, string? detected, string expected)
    {
        var result = await DictationTextPipeline.ProcessAsync("road", new()
        {
            GermanOutputVariant = GermanOutputVariant.Switzerland
        }, configured, detectedLanguage: detected, correctDictionary: _ => "Straße");
        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public async Task OptionalStepFailuresRetainPrecedingTextAndAreReported()
    {
        var result = await DictationTextPipeline.ProcessAsync("two.", new(), "en",
            expandSnippets: (_, _) => throw new InvalidOperationException("Clipboard unavailable"),
            boostVocabulary: _ => throw new InvalidOperationException("Boosting unavailable"),
            correctDictionary: _ => throw new InvalidOperationException("Dictionary unavailable"));
        Assert.Equal("2.", result.Text);
        Assert.Equal(3, result.Warnings.Count);
        Assert.StartsWith("Snippet expansion", result.Warnings[0]);
        Assert.StartsWith("Vocabulary boosting", result.Warnings[1]);
        Assert.StartsWith("Dictionary corrections", result.Warnings[2]);
    }

    [Fact]
    public async Task SnippetFailureDoesNotSkipLaterCorrection()
    {
        var result = await DictationTextPipeline.ProcessAsync("Hello.", new(), "en",
            expandSnippets: (_, _) => throw new InvalidOperationException(),
            correctDictionary: text => text.Replace("Hello", "Welcome"));
        Assert.Equal("Welcome.", result.Text);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task CancellationDuringAsyncSnippetAbortsBeforeCorrections()
    {
        using var cancellation = new CancellationTokenSource();
        var corrected = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DictationTextPipeline.ProcessAsync("Hello", new(), "en",
            expandSnippets: async (text, ct) =>
            {
                await Task.Yield();
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return text;
            }, correctDictionary: text => { corrected = true; return text; }, ct: cancellation.Token));
        Assert.False(corrected);
    }
}
