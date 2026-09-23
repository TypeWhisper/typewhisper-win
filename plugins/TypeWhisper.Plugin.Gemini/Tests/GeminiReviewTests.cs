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

    [Theory]
    [InlineData("gemini-flash-latest", false)]
    [InlineData("gemini-3.5-transcribe", false)]
    [InlineData("gemini-3.6-transcribe-live", false)]
    [InlineData("gemini-3.5-transcribe-live-extra", false)]
    [InlineData("gemini-3.5-transcribe-live", true)]
    [InlineData("models/GEMINI-3.5-TRANSCRIBE-LIVE", true)]
    public async Task PersistedStreamingModelMustBeTheSelectedBatchModelsLiveSibling(string liveModel, bool supported)
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        host.SetSetting("fetchedTranscriptionModels.v1", new List<GeminiFetchedTranscriptionModel>
        { new("gemini-3.5-transcribe", null, liveModel) });
        using var plugin = new GeminiPlugin(); await plugin.ActivateAsync(host);
        Assert.Equal("gemini-3.5-transcribe", plugin.SelectedModelId);
        Assert.Equal(supported, plugin.SupportsStreaming);
        Assert.Equal(supported, plugin.SupportsStreamingCompletion);
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
    [InlineData("{\"choices\":null}")]
    [InlineData("{\"choices\":[null]}")]
    [InlineData("{\"choices\":[7]}")]
    [InlineData("{\"choices\":[\"text\"]}")]
    [InlineData("{\"choices\":[{\"message\":null}]}")]
    [InlineData("{\"choices\":[{\"message\":[]}]}")]
    [InlineData("{\"choices\":[{\"message\":7}]}")]
    public async Task MalformedChatResponsesAreTypedFailures(string body)
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
        string? uploadedName = null;
        using var http = new HttpClient(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Delete) { Assert.EndsWith(uploadedName!, request.RequestUri!.AbsolutePath); deleted = true; return new(HttpStatusCode.OK); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                using var metadata = System.Text.Json.JsonDocument.Parse(body!);
                uploadedName = metadata.RootElement.GetProperty("file").GetProperty("name").GetString();
                var started = new HttpResponseMessage(HttpStatusCode.OK);
                started.Headers.TryAddWithoutValidation("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/fixture");
                return started;
            }
            Assert.Equal("/upload/fixture", request.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new CancelDuringMetadataContent(cancel, uploadedName!) };
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeminiTranscriptionClient.TranscribeAsync(http,
            "https://generativelanguage.googleapis.com/v1beta", "fixture", "gemini-3.5-transcribe", [1,2], [], [], GeminiTranscriptionMode.Smart, null, cancel.Token));
        Assert.True(deleted);
    }

    [Fact]
    public async Task UploadMetadataDeadlineIsReportedAsTimeoutWithoutCallerCancellation()
    {
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string? uploadedName = null;
        var deleted = false;
        using var http = new HttpClient(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Delete) { Assert.EndsWith(uploadedName!, request.RequestUri!.AbsolutePath); deleted = true; return new(HttpStatusCode.OK); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                using var metadata = System.Text.Json.JsonDocument.Parse(body!);
                uploadedName = metadata.RootElement.GetProperty("file").GetProperty("name").GetString();
                var started = new HttpResponseMessage(HttpStatusCode.OK);
                started.Headers.TryAddWithoutValidation("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/fixture");
                return started;
            }
            Assert.Equal("/upload/fixture", request.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StalledMetadataContent() };
        }));
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => GeminiTranscriptionClient.TranscribeAsync(http,
            "https://generativelanguage.googleapis.com/v1beta", "fixture", "gemini-3.5-transcribe", [1,2], [], [], GeminiTranscriptionMode.Smart, null, caller.Token));
        Assert.Equal(PluginRequestFailureKind.Timeout, error.FailureKind);
        Assert.False(caller.IsCancellationRequested);
        Assert.True(deleted);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"file\":{\"name\":\"files/unrelated\"}}")]
    [InlineData("{\"file\":{\"name\":\"files/unrelated\",\"uri\":\"https://generativelanguage.googleapis.com/v1beta/files/unrelated\"}}")]
    public async Task InvalidUploadMetadataDeletesOnlyThePreallocatedResource(string response)
    {
        string? allocated = null;
        var deleted = new List<string>();
        using var http = new HttpClient(new CapturingHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Delete) { deleted.Add(request.RequestUri!.AbsolutePath); return new(HttpStatusCode.NoContent); }
            if (request.RequestUri!.AbsolutePath == "/upload/v1beta/files")
            {
                using var metadata = System.Text.Json.JsonDocument.Parse(body!);
                allocated = metadata.RootElement.GetProperty("file").GetProperty("name").GetString();
                Assert.Matches("^files/tw-[a-f0-9]{32}$", allocated!);
                var started = new HttpResponseMessage(HttpStatusCode.OK);
                started.Headers.Add("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/fixture");
                return started;
            }
            Assert.Equal("/upload/fixture", request.RequestUri.AbsolutePath);
            return JsonResponse(response);
        }));
        await Assert.ThrowsAsync<PluginRequestException>(() => GeminiTranscriptionClient.TranscribeAsync(http,
            "https://generativelanguage.googleapis.com/v1beta", "fixture", "gemini-3.5-transcribe", [1,2], [], [], GeminiTranscriptionMode.Smart, null, default));
        Assert.Equal("/v1beta/" + allocated, Assert.Single(deleted));
        Assert.DoesNotContain("/v1beta/files/unrelated", deleted);
    }

    [Fact]
    public async Task SupportedIsoLanguagesAreAvailableToTheHostAndMapToDocumentedLocaleHints()
    {
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        using var http = new HttpClient();
        using var plugin = new GeminiPlugin(http, (_, _, languages, _, _, _) =>
        {
            Assert.Equal(new[] { "de-DE", "kea-CV", "yue-Hant-HK", "hy-AM" }, languages);
            return Task.FromResult<IStreamingSession>(new StubStream());
        });
        await plugin.ActivateAsync(host);
        ITranscriptionEnginePlugin engine = plugin;
        Assert.Contains("en", engine.SupportedLanguages);
        Assert.Contains("de", engine.SupportedLanguages);
        Assert.Contains("kea", engine.SupportedLanguages);
        Assert.DoesNotContain("eo", engine.SupportedLanguages);
        Assert.Equal(engine.SupportedLanguages.Count, engine.TranscriptionModels[0].LanguageCount);
        await using var stream = await engine.StartStreamingWithLanguageHintsAsync(["de", "kea", "yue", "hy"], default);
    }

    private sealed class StalledMetadataContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class CancelDuringMetadataContent(CancellationTokenSource cancel, string name) : HttpContent
    {
        private readonly byte[] _body = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { file = new { name, uri = "https://generativelanguage.googleapis.com/v1beta/" + name } }));
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
