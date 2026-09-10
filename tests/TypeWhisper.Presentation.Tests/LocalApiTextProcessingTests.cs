using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiTextProcessingTests
{
    [Fact]
    public void TranslationCapturesDefaultProviderAndRequestedTarget()
    {
        var plan = LocalApiTranslation.Prepare("de", new("fixture-provider", "fixture-model"),
            (provider, model) => provider == "fixture-provider" && model == "fixture-model");
        Assert.Equal("fixture-provider", plan.Provider);
        Assert.Equal("fixture-model", plan.Model);
        Assert.Contains("into de", plan.Prompt);
        Assert.Contains("Return only the translated text", plan.Prompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TranslationRejectsMissingOrUnavailableDefaultBeforeProcessing(bool configured)
    {
        var error = Assert.Throws<LocalApiRequestException>(() => LocalApiTranslation.Prepare("de",
            configured ? new("fixture", "model") : null, (_, _) => false));
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task CorrectionsRunAfterTranslation()
    {
        var result = await LocalApiTextProcessing.ProcessAsync("hello", true,
            (text, _) => Task.FromResult(text == "hello" ? "hallo" : throw new Exception()),
            text => text == "hallo" ? "Hallo!" : throw new Exception(), default);
        Assert.Equal("Hallo!", result);
    }

    [Fact]
    public async Task NoCorrectionsKeepsTranslatedTextAndDoesNotCallDictionary()
    {
        Assert.Equal("hallo", await LocalApiTextProcessing.ProcessAsync("hello", false,
            (_, _) => Task.FromResult("hallo"), _ => throw new Exception(), default));
    }

    [Fact]
    public async Task NoTranslationRetainsRawText()
    {
        Assert.Equal("raw text", await LocalApiTextProcessing.ProcessAsync("raw text", false, null, null, default));
    }

    [Fact]
    public async Task CancellationRejectsLateTranslationBeforeCorrection()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalApiTextProcessing.ProcessAsync("hello", true,
            (_, _) => { cancellation.Cancel(); return Task.FromResult("late"); },
            _ => throw new Exception(), cancellation.Token));
    }

    [Fact]
    public async Task EmptyTranslationIsNotReportedAsSuccess()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalApiTextProcessing.ProcessAsync("hello", true,
            (_, _) => Task.FromResult(" "), null, default));
    }
}
