using System.Net;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    [Fact]
    public async Task ActivationSkipsMalformedPersistedIdsAndKeepsStrictCallerValidation()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel> { new("bad id", null), new("gemini-flash-latest", null) });
        host.SetSetting("fetchedTranscriptionModels.v1", new List<GeminiFetchedTranscriptionModel>
        {
            new("bad?id", null, null), new("gemini-3.5-transcribe", null, "bad live id")
        });
        host.SetSetting("selectedTranscriptionModel", "bad persisted id");
        using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        Assert.Equal("gemini-flash-latest", Assert.Single(plugin.SupportedModels).Id);
        Assert.Equal("gemini-3.5-transcribe", Assert.Single(plugin.TranscriptionModels).Id);
        Assert.Equal("gemini-3.5-transcribe", plugin.SelectedModelId);
        Assert.False(plugin.SupportsStreaming);
        Assert.Throws<ArgumentException>(() => plugin.SelectModel("bad id"));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.ProcessAsync("", "test", "bad id", default));
    }

    [Fact]
    public async Task ModelDiscoverySkipsMalformedNativeIds()
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse("""{"models":[{"name":"models/bad id"},{"name":"models/gemini-flash-latest"},{"name":"models/gemini-3.5-transcribe"},{"name":"models/gemini-3.5-transcribe-live?bad"}]}""")));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var catalog = await plugin.FetchModelCatalogAsync();
        Assert.NotNull(catalog);
        Assert.Single(catalog.LlmModels);
        Assert.Null(Assert.Single(catalog.TranscriptionModels).LiveModelId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"models\":null}")]
    public async Task ConnectionCheckClassifiesMalformedSuccess(string body)
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse(body)));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
        Assert.Equal(PluginRequestFailureKind.OutputIncomplete, failure.FailureKind);
    }

    [Fact]
    public async Task ConnectionCheckForwardsCallerCancellationToTransport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new WaitingValidationHandler(entered));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        using var cancellation = new CancellationTokenSource();
        var check = plugin.ValidateConfigurationAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    private sealed class WaitingValidationHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
