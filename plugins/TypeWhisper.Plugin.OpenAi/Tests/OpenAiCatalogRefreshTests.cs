using System.Net;
using System.Text;
using TypeWhisper.Plugin.OpenAi;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string RefreshedCatalog = """{"data":[{"id":"gpt-5.5"},{"id":"gpt-4o"},{"id":"whisper-1"},{"id":"gpt-transcribe"}]}""";

    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    public async Task Activation_RefreshesAnUndatedOrDayOldApiCatalogAndSavesItsDate(int? hoursOld)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer sk-live", request.Headers.Authorization?.ToString());
            await release.Task;
            return CatalogResponse(RefreshedCatalog);
        });
        var host = CatalogHost("sk-live", hoursOld is { } hours ? RefreshNow.AddHours(-hours) : null);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        // The saved catalog stays in use until the refresh delivers a new one.
        Assert.Equal(["gpt-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
        release.SetResult();
        await sut.ApiCatalogRefreshTask;

        Assert.Single(handler.Requests);
        Assert.Equal(["gpt-4o", "gpt-5.5"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["gpt-transcribe", "whisper-1"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        var snapshot = host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!;
        Assert.True(snapshot.HasFetched);
        Assert.Equal(RefreshNow, snapshot.FetchedAt);
        Assert.Equal(RefreshNow, sut.ApiCatalogFetchedAt);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task Activation_RefreshesACatalogSavedBeforeTheSnapshotWithoutDiscardingItFirst()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return CatalogResponse(RefreshedCatalog);
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        host.SetSetting("fetchedLLMModels", new List<OpenAiFetchedModel> { new("gpt-4.1-mini", "openai") });
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        Assert.Equal(["gpt-4.1-mini"], sut.SupportedModels.Select(m => m.Id).ToArray());
        release.SetResult();
        await sut.ApiCatalogRefreshTask;

        Assert.Equal(["gpt-4o", "gpt-5.5"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(RefreshNow, host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.FetchedAt);
    }

    [Fact]
    public async Task Activation_KeepsACatalogYoungerThanADayWithoutRequests()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        var host = CatalogHost("sk-live", RefreshNow.AddHours(-23));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.ApiCatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.Equal(["gpt-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ChatGptLoginAloneNeverRefreshesInTheBackground()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected background request."));
        var host = new TestPluginHostServices();
        host.SetSetting("authMode", "chatgpt");
        // An expired login would need a token refresh, which must never happen in the background.
        host.SetSetting("oauthExpiresAt", RefreshNow.AddHours(-1));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.ApiCatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.True(sut.IsAvailable);
    }

    [Fact]
    public async Task ChatGptModeRefreshesOnlyTheApiCatalogInTheBackground()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(RefreshedCatalog)));
        var host = CatalogHost("sk-live", fetchedAt: null);
        host.SetSetting("authMode", "chatgpt");
        host.SetSetting("oauthExpiresAt", RefreshNow.AddHours(-1));
        host.Secrets["oauth-access-token"] = "expired-access-token";
        host.Secrets["oauth-refresh-token"] = "refresh-token";
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.ApiCatalogRefreshTask;

        Assert.Equal(["https://api.openai.com/v1/models"], handler.Requests);
        Assert.Equal(["gpt-transcribe", "whisper-1"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal("refresh-token", host.Secrets["oauth-refresh-token"]);
        Assert.False(host.Secrets.ContainsKey("chatgpt-session"));
    }

    [Fact]
    public async Task Activation_WithoutApiKeyDoesNotRefresh()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.ApiCatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.Equal(OpenAiPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task DamagedFetchDateReadsAsNoDateAndRefreshes()
    {
        var handler = new CatalogHandler(_ => Task.FromResult(CatalogResponse(RefreshedCatalog)));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "sk-live";
        host.SetSetting("apiModelCatalogSnapshot", new
        {
            HasFetched = true,
            LlmModels = new[] { new OpenAiFetchedModel("gpt-4.1", "openai") },
            TranscriptionModels = new[] { new OpenAiFetchedModel("whisper-1", "openai") },
            FetchedAt = "not a date",
        });
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.ApiCatalogRefreshTask;

        Assert.Single(handler.Requests);
        Assert.Equal(RefreshNow, host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.FetchedAt);
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, like a slow server whose answer arrives after deactivation.
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return CatalogResponse(RefreshedCatalog);
        });
        var host = CatalogHost("sk-live", fetchedAt: null);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.ApiCatalogRefreshTask;

        var snapshot = host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!;
        Assert.Null(snapshot.FetchedAt);
        Assert.Equal("gpt-saved", Assert.Single(snapshot.LlmModels).Id);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task KeyChange_DiscardsTheOldAccountsCatalogAndRefreshesWithTheNewKey()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            if (request.Headers.Authorization?.Parameter == "sk-first")
            {
                await releaseFirst.Task;
                return CatalogResponse("""{"data":[{"id":"gpt-first-only"},{"id":"whisper-1"}]}""");
            }

            Assert.Equal("Bearer sk-second", request.Headers.Authorization?.ToString());
            await releaseSecond.Task;
            return CatalogResponse(RefreshedCatalog);
        });
        var host = CatalogHost("sk-first", fetchedAt: null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.ApiCatalogRefreshTask;

        await sut.SetApiKeyAsync("sk-second");

        // The first account's models are gone before the new catalog arrives.
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "gpt-saved");
        Assert.False(host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.HasFetched);
        releaseSecond.SetResult();
        await sut.ApiCatalogRefreshTask;
        // The refresh started with the first key answers last and is dropped.
        releaseFirst.SetResult();
        await firstRefresh;

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["gpt-4o", "gpt-5.5"], sut.SupportedModels.Select(m => m.Id).ToArray());
        var snapshot = host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!;
        Assert.Equal(["gpt-4o", "gpt-5.5"], snapshot.LlmModels.Select(m => m.Id).ToArray());
        Assert.Equal(RefreshNow, snapshot.FetchedAt);
    }

    [Fact]
    public async Task AuthModeChange_DropsARunningRefreshAndRefreshesAgain()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var handler = new CatalogHandler(async _ =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                // Signals from inside the first request, so the auth mode change cannot overtake it.
                firstStarted.SetResult();
                await releaseFirst.Task;
                return CatalogResponse("""{"data":[{"id":"gpt-before-switch"},{"id":"whisper-1"}]}""");
            }

            return CatalogResponse(RefreshedCatalog);
        });
        var host = CatalogHost("sk-live", fetchedAt: null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var firstRefresh = sut.ApiCatalogRefreshTask;

        sut.SetAuthMode(OpenAiAuthMode.ChatGpt);
        await sut.ApiCatalogRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        releaseFirst.SetResult();
        await firstRefresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.Requests.Count);
        var snapshot = host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!;
        Assert.Equal(["gpt-4o", "gpt-5.5"], snapshot.LlmModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ManualRefreshStartedBeforeAKeyChangeIsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return CatalogResponse("""{"data":[{"id":"gpt-first-only"},{"id":"whisper-1"}]}""");
        });
        var host = CatalogHost("sk-first", RefreshNow);
        using var sut = new OpenAiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        var refresh = sut.RefreshAvailableLlmModelsAsync();
        await handler.Started;
        await sut.SetApiKeyAsync("sk-second");
        release.SetResult();

        Assert.Empty(await refresh);
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "gpt-first-only");
        Assert.False(host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.HasFetched);
    }

    [Fact]
    public async Task ManualRefreshStartedDuringAKeyChangeIsDropped()
    {
        var storeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(_ =>
            Task.FromResult(CatalogResponse("""{"data":[{"id":"gpt-first-only"},{"id":"whisper-1"}]}""")));
        var host = CatalogHost("sk-first", RefreshNow);
        using var sut = new OpenAiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);
        host.BeforeStoreSecret = async () => { storeStarted.TrySetResult(); await releaseStore.Task; };

        // The refresh runs while the key change waits in its secret write, so it still sends the old key.
        var keyChange = sut.SetApiKeyAsync("sk-second");
        await storeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var models = await sut.RefreshAvailableLlmModelsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        releaseStore.SetResult();
        await keyChange.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(models);
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "gpt-first-only");
        var snapshot = host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!;
        Assert.False(snapshot.HasFetched);
        Assert.Null(snapshot.FetchedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, """{"data":[]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"id":"gpt-5.5"}]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"id":"whisper-1"}]}""")]
    public async Task FailedOrEmptyBackgroundRefreshKeepsTheCurrentCatalog(HttpStatusCode status, string body)
    {
        var dayOld = RefreshNow.AddDays(-1);
        var handler = new CatalogHandler(_ => Task.FromResult(CatalogResponse(body, status)));
        var host = CatalogHost("sk-live", dayOld);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.ApiCatalogRefreshTask;

        Assert.Single(handler.Requests);
        Assert.Equal(["gpt-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["whisper-1"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(dayOld, host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.FetchedAt);
        Assert.Single(host.Warnings);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SavedModelsSurviveACatalogThatLacksThemAndApplyAgainOnceOffered()
    {
        var body = """{"data":[{"id":"chatgpt-4o-latest"},{"id":"gpt-5.5"},{"id":"o4-mini"},{"id":"gpt-transcribe"},{"id":"gpt-4o-mini-transcribe"}]}""";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return CatalogResponse(body);
        });
        var host = CatalogHost("sk-live", fetchedAt: null, savedModel: "gpt-4.1");
        host.SetSetting("llmSelection", new OpenAiPlugin.LlmSelectionSnapshot("api-key", "gpt-4.1"));
        host.SetSetting("selectedModel", "whisper-1");
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        Assert.Equal("gpt-4.1", sut.SelectedLlmModelId);
        release.SetResult();
        await sut.ApiCatalogRefreshTask;

        // GPT-5.5 and GPT Transcribe stand in, not the alphabetically first entries; the saved values stay.
        Assert.Equal(OpenAiPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal("gpt-transcribe", sut.SelectedModelId);
        Assert.Equal("gpt-4.1", host.GetSetting<OpenAiPlugin.LlmSelectionSnapshot>("llmSelection")!.ModelId);
        Assert.Equal("whisper-1", host.GetSetting<string>("selectedModel"));

        body = """{"data":[{"id":"gpt-4.1"},{"id":"gpt-5.5"},{"id":"whisper-1"}]}""";
        await sut.ExecuteSettingsActionAsync("refresh", CancellationToken.None);

        Assert.Equal("gpt-4.1", sut.SelectedLlmModelId);
        Assert.Equal("whisper-1", sut.SelectedModelId);
    }

    [Fact]
    public async Task RefreshButtonRecordsTheFetchDate()
    {
        var handler = new CatalogHandler(_ => Task.FromResult(CatalogResponse(RefreshedCatalog)));
        var host = CatalogHost("sk-live", RefreshNow.AddDays(-2));
        using var sut = new OpenAiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        Assert.Equal("Model list refreshed.", await sut.ExecuteSettingsActionAsync("refresh", CancellationToken.None));

        Assert.Equal(RefreshNow, sut.ApiCatalogFetchedAt);
        Assert.Equal(RefreshNow, host.GetSetting<OpenAiPlugin.ApiCatalogSnapshot>("apiModelCatalogSnapshot")!.FetchedAt);
    }

    private static OpenAiPlugin CreateRefreshingPlugin(HttpMessageHandler handler) =>
        new(new HttpClient(handler), _ => new FakeTtsPlaybackSession(), refreshApiModelsAutomatically: true, clock: new TestClock(RefreshNow));

    // The saved text model is not in the built-in list, so tests can tell the two apart.
    private static TestPluginHostServices CatalogHost(string apiKey, DateTimeOffset? fetchedAt, string savedModel = "gpt-saved")
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = apiKey;
        host.SetSetting("apiModelCatalogSnapshot", new OpenAiPlugin.ApiCatalogSnapshot(true,
            [new(savedModel, "openai")], [new("whisper-1", "openai")], fetchedAt));
        return host;
    }

    private static HttpResponseMessage CatalogResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers model list requests and records them.</summary>
    private sealed class CatalogHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _requests = [];

        public Task Started => _started.Task;

        public List<string> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request.RequestUri!.ToString());
            _started.TrySetResult();
            return respond(request);
        }
    }
}
