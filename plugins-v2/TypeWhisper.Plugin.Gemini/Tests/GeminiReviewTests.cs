using System.Net;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    [Fact]
    public async Task NativeModelDiscoveryUsesAdvertisedGenerationMethods()
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse("""
            {"models":[
                {"name":"models/gemini-chat","supportedGenerationMethods":["generateContent"]},
                {"name":"models/gemma-chat","supportedGenerationMethods":["countTokens","generateContent"]},
                {"name":"models/gemini-unsupported","supportedGenerationMethods":["countTokens"]},
                {"name":"models/gemini-empty","supportedGenerationMethods":[]},
                {"name":"models/gemini-legacy"}
            ]}
            """)));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var catalog = await plugin.FetchModelCatalogAsync();
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "gemini-chat", "gemini-legacy", "gemma-chat" }, catalog.LlmModels.Select(m => m.Id));
    }

    [Fact]
    public async Task OversizedTextRequestReportsTextLimitWithoutAudioAdvice()
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => new(HttpStatusCode.RequestEntityTooLarge)
        { Content = new StringContent("{}") }));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var failure = await Assert.ThrowsAsync<PluginRequestException>(() =>
            plugin.ProcessAsync("", "fixture", "gemini-flash-latest", default));
        Assert.Equal(PluginRequestFailureKind.RequestTooLarge, failure.FailureKind);
        Assert.Equal(413, failure.HttpStatusCode);
        Assert.Contains("text request", failure.Message);
        Assert.DoesNotContain("Audio", failure.Message);
        Assert.DoesNotContain("25 MB", failure.Message);
    }

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

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    public async Task NonObjectChatResponsesAreTypedFailures(string body)
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse(body)));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("", "fixture", "gemini-flash-latest", default));
        Assert.Equal(PluginRequestFailureKind.EmptyResponse, error.FailureKind);
    }

    [Fact]
    public async Task CatalogCapabilitiesRemainIndependentAndEmptyCatalogSurvivesRestart()
    {
        using var http = new HttpClient(new CapturingHandler((_, _) => JsonResponse("""{"models":[]}""")));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var plugin = new GeminiPlugin(http); await plugin.ActivateAsync(host);
        await plugin.SetFetchedModelCatalogAsync(new([], [new("gemini-3.5-transcribe", null, null)], DateTimeOffset.UtcNow));
        Assert.True(plugin.IsConfigured);
        Assert.False(plugin.IsAvailable);
        Assert.Empty(plugin.SupportedModels);
        Assert.NotNull(plugin.TextSettings);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Empty(plugin.SupportedModels);
        await plugin.SetFetchedModelCatalogAsync(new([new("gemini-flash-latest", null)], [], DateTimeOffset.UtcNow));
        Assert.True(plugin.IsAvailable);
        Assert.False(plugin.IsConfigured);
        Assert.True(((IApiKeyPlugin)plugin).IsConfigured);
        Assert.True(plugin.ShouldRefreshModelCatalog(DateTimeOffset.UtcNow.AddDays(7)));
        await plugin.ValidateConfigurationAsync(default);
    }

    [Fact]
    public async Task QueuedCatalogRefreshKeepsSelectionCommittedWhileItWaited()
    {
        var host = new TestPluginHostServices();
        using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        var catalog = new GeminiModelCatalog([], [new("gemini-3.5-transcribe", null, null), new("gemini-3.6-transcribe", null, null)], DateTimeOffset.UtcNow);
        await plugin.SetFetchedModelCatalogAsync(catalog);
        plugin.SelectModel("gemini-3.5-transcribe");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        host.BeforeSetSetting = key =>
        {
            if (key != "selectedTranscriptionModel") return;
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var select = Task.Run(() => plugin.SelectModel("gemini-3.6-transcribe"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = plugin.SetFetchedModelCatalogAsync(catalog);
        Assert.False(refresh.IsCompleted);
        release.Set();
        await select; await refresh;
        Assert.Equal("gemini-3.6-transcribe", plugin.SelectedModelId);
    }

    [Fact]
    public async Task CancellationDuringUploadMetadataStillDeletesCompletedUpload()
    {
        using var cancel = new CancellationTokenSource();
        var deleted = false;
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) { deleted = true; return new(HttpStatusCode.OK); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                var started = new HttpResponseMessage(HttpStatusCode.OK);
                started.Headers.TryAddWithoutValidation("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/fixture");
                return started;
            }
            Assert.Equal("/upload/fixture", request.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new CancelDuringMetadataContent(cancel) };
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeminiTranscriptionClient.TranscribeAsync(http,
            "https://generativelanguage.googleapis.com/v1beta", "fixture", "gemini-3.5-transcribe", [1,2], [], [], GeminiTranscriptionMode.Smart, null, cancel.Token));
        Assert.True(deleted);
    }

    private sealed class CancelDuringMetadataContent(CancellationTokenSource cancel) : HttpContent
    {
        private readonly byte[] _body = System.Text.Encoding.UTF8.GetBytes("""{"file":{"name":"files/fixture","uri":"https://generativelanguage.googleapis.com/v1beta/files/fixture"}}""");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            cancel.Cancel();
            await stream.WriteAsync(_body);
        }
        protected override bool TryComputeLength(out long length) { length = _body.Length; return true; }
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
