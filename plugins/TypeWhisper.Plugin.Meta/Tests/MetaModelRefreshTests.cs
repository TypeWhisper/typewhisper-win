using System.Net;
using System.Net.Http;
using System.Text;
using PortableMigration.Tests;

namespace TypeWhisper.Plugin.Meta.Tests;

public sealed partial class MetaPluginTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string RefreshedCatalog = """{"data":[{"id":"muse-spark-2.0"},{"id":"muse-voice-transcribe-2.0"}]}""";

    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    [InlineData(-1)]
    public async Task Activation_RefreshesAnUndatedDayOldOrFutureCatalogAndSavesItsDate(int? hoursOld)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            Assert.Equal("https://api.meta.ai/v1/models", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer meta-key", request.Headers.Authorization?.ToString());
            await release.Task;
            return Json(RefreshedCatalog);
        });
        var host = CatalogHost("meta-key", hoursOld is { } hours ? RefreshNow.AddHours(-hours) : null);
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(host);
        await handler.Started;
        // The saved list stays in use until the refresh delivers a new one.
        Assert.Equal("muse-spark-1.1", Assert.Single(sut.SupportedModels).Id);
        release.SetResult();
        await sut.ModelRefreshTask;

        Assert.Equal(1, handler.Requests);
        Assert.Equal("muse-spark-2.0", Assert.Single(sut.SupportedModels).Id);
        Assert.Equal("muse-voice-transcribe-2.0", Assert.Single(sut.TranscriptionModels).Id);
        Assert.Equal(RefreshNow, sut.FetchedModelsAt);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        Assert.Equal("muse-spark-2.0", Assert.Single(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels")!).Id);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task Activation_KeepsACatalogYoungerThanADayWithoutRequests()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        var host = CatalogHost("meta-key", RefreshNow.AddHours(-23));
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal(0, handler.Requests);
        Assert.Equal("muse-spark-1.1", Assert.Single(sut.SupportedModels).Id);
    }

    [Fact]
    public async Task Activation_WithoutApiKeyDoesNotRefresh()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(CatalogHost(apiKey: null, fetchedAt: null));
        await sut.ModelRefreshTask;

        Assert.Equal(0, handler.Requests);
        Assert.Equal("muse-spark-1.1", Assert.Single(sut.SupportedModels).Id);
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, like a slow server whose answer arrives after deactivation.
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return Json(RefreshedCatalog);
        });
        var host = CatalogHost("meta-key", fetchedAt: null);
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.ModelRefreshTask;

        Assert.Null(sut.FetchedModelsAt);
        Assert.Null(host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        Assert.Equal("muse-spark-1.1", Assert.Single(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels")!).Id);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task KeyChange_DiscardsTheOldAccountsCatalogAndRefreshesWithTheNewKey()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            if (request.Headers.Authorization?.Parameter == "first-key")
            {
                await releaseFirst.Task;
                return Json("""{"data":[{"id":"muse-spark-first"},{"id":"muse-voice-transcribe-first"}]}""");
            }

            Assert.Equal("Bearer second-key", request.Headers.Authorization?.ToString());
            await releaseSecond.Task;
            return Json(RefreshedCatalog);
        });
        var host = CatalogHost("first-key", fetchedAt: null);
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.ModelRefreshTask;

        await sut.SetApiKeyAsync("second-key");

        // The first account's models are gone before the new list arrives.
        Assert.Equal(0, sut.FetchedLlmModelCount);
        Assert.Empty(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels")!);
        Assert.Null(host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        releaseSecond.SetResult();
        await sut.ModelRefreshTask;
        // The refresh started with the first key answers last and is dropped.
        releaseFirst.SetResult();
        await firstRefresh;

        Assert.Equal(2, handler.Requests);
        Assert.Equal("muse-spark-2.0", Assert.Single(sut.SupportedModels).Id);
        Assert.Equal("muse-spark-2.0", Assert.Single(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels")!).Id);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        Assert.Equal("second-key", host.Secrets["api-key"]);
    }

    [Fact]
    public async Task ManualRefreshStartedWithTheOldKeyIsDroppedAfterAKeyChange()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return Json(RefreshedCatalog);
        });
        var host = CatalogHost("first-key", RefreshNow);
        using var sut = new MetaPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        var refresh = sut.RefreshAvailableModelsAsync();
        await handler.Started;
        await sut.SetApiKeyAsync("second-key");
        release.SetResult();

        Assert.Null(await refresh);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, sut.FetchedLlmModelCount);
        Assert.Empty(host.GetSetting<List<MetaFetchedModel>>("fetchedLlmModels")!);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, """{"data":[]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"id":"muse-image-1.0"}]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"id":"muse-spark-2.0"}]}""")]
    public async Task FailedOrEmptyBackgroundRefreshKeepsTheCurrentCatalog(HttpStatusCode status, string body)
    {
        var dayOld = RefreshNow.AddDays(-1);
        var handler = new CatalogHandler(_ => Task.FromResult(Json(body, status)));
        var host = CatalogHost("meta-key", dayOld);
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal(1, handler.Requests);
        Assert.Equal("muse-spark-1.1", Assert.Single(sut.SupportedModels).Id);
        Assert.Equal("muse-voice-transcribe-1.0", Assert.Single(sut.TranscriptionModels).Id);
        Assert.Equal(dayOld, sut.FetchedModelsAt);
        Assert.Equal(dayOld, host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
        Assert.Single(host.Warnings);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SavedModelsSurviveARefreshThatLacksThemAndApplyAgainOnceOffered()
    {
        var body = """{"data":[{"id":"muse-spark-2.0"},{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-2.0"}]}""";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async _ =>
        {
            await release.Task;
            return Json(body);
        });
        var host = CatalogHost("meta-key", fetchedAt: null);
        host.SetSetting("selectedLlmModel", "muse-spark-1.1");
        host.SetSetting("selectedModel", "muse-voice-transcribe-1.0");
        using var sut = new MetaPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

        await sut.ActivateAsync(host);
        Assert.Equal("muse-spark-1.1", sut.SelectedLlmModelId);
        release.SetResult();
        await sut.ModelRefreshTask;

        // Requests use the default while the saved models are missing; the saved values stay.
        Assert.Equal(MetaPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal("muse-voice-transcribe-2.0", sut.SelectedModelId);
        Assert.Equal("muse-spark-1.1", host.GetSetting<string>("selectedLlmModel"));
        Assert.Equal("muse-voice-transcribe-1.0", host.GetSetting<string>("selectedModel"));

        body = """{"data":[{"id":"muse-spark-1.1"},{"id":"muse-spark-1.2"},{"id":"muse-voice-transcribe-1.0"}]}""";
        await sut.ExecuteSettingsActionAsync("refresh", CancellationToken.None);

        Assert.Equal("muse-spark-1.1", sut.SelectedLlmModelId);
        Assert.Equal("muse-voice-transcribe-1.0", sut.SelectedModelId);
    }

    [Fact]
    public async Task RefreshButtonRecordsTheFetchDate()
    {
        var handler = new CatalogHandler(_ => Task.FromResult(Json(RefreshedCatalog)));
        var host = CatalogHost("meta-key", RefreshNow.AddHours(-30));
        using var sut = new MetaPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        var message = await sut.ExecuteSettingsActionAsync("refresh", CancellationToken.None);

        Assert.Equal("Models updated: 1 transcription, 1 text.", message);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(RefreshNow, sut.FetchedModelsAt);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedModelsAtUtc"));
    }

    private static FakePluginHostServices CatalogHost(string? apiKey, DateTimeOffset? fetchedAt)
    {
        var host = new FakePluginHostServices();
        if (apiKey is not null)
            host.Secrets["api-key"] = apiKey;
        host.SetSetting("fetchedLlmModels", new List<MetaFetchedModel> { new("muse-spark-1.1", "meta") });
        host.SetSetting("fetchedTranscriptionModels", new List<MetaFetchedModel> { new("muse-voice-transcribe-1.0", "meta") });
        if (fetchedAt is not null)
            host.SetSetting("fetchedModelsAtUtc", fetchedAt);
        return host;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>Answers model list requests and counts them.</summary>
internal sealed class CatalogHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);
    public Task Started => _started.Task;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requests);
        _started.TrySetResult();
        return respond(request);
    }
}
