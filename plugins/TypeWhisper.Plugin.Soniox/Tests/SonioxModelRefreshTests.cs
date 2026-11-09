using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.Plugin.Soniox;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class SonioxFeatureTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private const string V7ModelsJson = """
        {
          "models": [
            { "id": "stt-rt-v7", "transcription_mode": "real_time", "languages": [ { "code": "de" } ] },
            { "id": "stt-async-v7", "transcription_mode": "async", "languages": [] }
          ]
        }
        """;

    private const string OneVoiceJson = """
        { "models": [ { "id": "tts-rt-v2", "languages": [], "voices": [ { "id": "Daniel" } ] } ] }
        """;

    [Fact]
    public void PublicConstructor_KeepsTheBackgroundRefreshOffInTestProcesses()
    {
        // The test project sets this switch, so the package test, which loads the built plugin through its
        // public constructor, cannot reach Soniox.
        Assert.False(TypeWhisper.Plugin.Shared.ModelCatalogAutoRefresh.AllowedInThisProcess);
        using var plugin = new SonioxPlugin();
        Assert.False(plugin.RefreshesModelsAutomatically);
        using var optedIn = new SonioxPlugin(new HttpClient(new FailingHandler()), backgroundCatalogRefresh: true);
        Assert.True(optedIn.RefreshesModelsAutomatically);
    }

    [Fact]
    public async Task Activation_RefreshesADayOldListInTheBackgroundAndSavesTheDate()
    {
        var requests = new ConcurrentQueue<string>();
        var handler = new RoutingHandler(request =>
        {
            requests.Enqueue($"{request.Headers.Authorization} {request.RequestUri}");
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/models" => Json(V7ModelsJson),
                "/v1/tts-models" => Json(TtsModelsJson),
                _ => throw new InvalidOperationException(request.RequestUri.ToString())
            };
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow - TimeSpan.FromHours(24) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        Assert.Equal(["Bearer key https://api.soniox.com/v1/models", "Bearer key https://api.soniox.com/v1/tts-models"], requests);
        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        var stored = host.GetSetting<SonioxCatalog>("catalog")!;
        Assert.Equal("us", stored.Region);
        Assert.Equal(RefreshNow, stored.FetchedAt);
        Assert.Equal(1, host.NotifyCount);
    }

    [Fact]
    public async Task Activation_RefreshesAListDatedInTheFuture()
    {
        var modelRequests = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/v1/models")
                return Json(TtsModelsJson);
            Interlocked.Increment(ref modelRequests);
            return Json(V7ModelsJson);
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddHours(2) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        Assert.Equal(1, modelRequests);
        Assert.Equal(RefreshNow, host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    [Fact]
    public async Task Activation_KeepsAListYoungerThanADayWithoutRequests()
    {
        var requests = 0;
        var handler = new RoutingHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddHours(-23) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        Assert.Equal(0, requests);
        Assert.Equal("stt-rt-v6", sut.EffectiveRealtimeModelId);
    }

    [Fact]
    public async Task Activation_DoesNotRefreshWithoutKey()
    {
        var requests = 0;
        var handler = new RoutingHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });

        using var sut = new SonioxPlugin(new HttpClient(handler), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(new FeatureHost());
        await sut.LastCleanupTask;

        Assert.True(sut.CatalogRefreshTask.IsCompleted);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var handler = new GatedHandler(request =>
            Json(request.RequestUri!.AbsolutePath == "/v1/models" ? V7ModelsJson : TtsModelsJson));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";

        using var sut = new SonioxPlugin(new HttpClient(handler), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await handler.Started;
        // Deactivation waits for the running refresh; the answer arrives only after it was cancelled.
        var deactivation = sut.DeactivateAsync();
        handler.Release();
        await deactivation;

        Assert.Null(host.GetSetting<SonioxCatalog>("catalog"));
        Assert.Equal(0, host.NotifyCount);
        Assert.DoesNotContain(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task KeyChange_RefreshesWithTheNewKeyAndKeepsTheListMeanwhile()
    {
        var handler = new GatedHandler(request =>
            Json(request.RequestUri!.AbsolutePath == "/v1/models" ? V7ModelsJson : TtsModelsJson));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "first-key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddHours(-1) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("second-key");
        await handler.Started;

        // Soniox lists the same models for every key of a region, so the cached list stays meanwhile.
        Assert.Equal("stt-rt-v6", sut.EffectiveRealtimeModelId);

        handler.Release();
        await sut.LastCleanupTask;

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.StartsWith("second-key ", request));
        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal(RefreshNow, host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    [Fact]
    public async Task KeyChange_DropsTheResultOfARefreshStartedWithTheOldKey()
    {
        var handler = new GatedHandler(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json(request.Headers.Authorization!.Parameter == "first-key" ? ModelsJson : V7ModelsJson)
            : Json(TtsModelsJson));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "first-key";

        using var sut = new SonioxPlugin(new HttpClient(handler), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.CatalogRefreshTask;

        await sut.SetApiKeyAsync("second-key");
        handler.Release();
        await firstRefresh;
        await sut.LastCleanupTask;

        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal("stt-rt-v7", host.GetSetting<SonioxCatalog>("catalog")!.SttModels[0].Id);
        Assert.DoesNotContain(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task ClearingTheKeyStartsNoRefresh()
    {
        var requests = 0;
        var handler = new RoutingHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddHours(-1) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("");
        await sut.LastCleanupTask;

        Assert.Equal(0, requests);
        Assert.False(sut.IsConfigured);
    }

    [Fact]
    public async Task RegionChange_LoadsTheNewRegionsListRightAwayEvenAfterAFailure()
    {
        var fail = true;
        var requests = new ConcurrentQueue<string>();
        var handler = new RoutingHandler(request =>
        {
            requests.Enqueue(request.RequestUri!.ToString());
            if (Volatile.Read(ref fail))
                return Json("""{ "error_message": "Invalid API key" }""", HttpStatusCode.Unauthorized);
            return Json(request.RequestUri.AbsolutePath == "/v1/models" ? V7ModelsJson : TtsModelsJson);
        });
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;
        Assert.Null(sut.CurrentCatalog);

        // After the failure, further dictations wait ten minutes, but another region loads at once.
        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        Assert.Single(requests);
        Volatile.Write(ref fail, false);
        sut.SetRegion("eu");
        await sut.LastCleanupTask;

        Assert.Equal(
            ["https://api.soniox.com/v1/models", "https://api.eu.soniox.com/v1/models", "https://api.eu.soniox.com/v1/tts-models"],
            requests);
        Assert.Equal("eu", sut.CurrentCatalog!.Region);
        Assert.Equal("eu", host.GetSetting<SonioxCatalog>("catalog")!.Region);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, """{ "error_message": "upstream down" }""")]
    [InlineData(HttpStatusCode.OK, """{ "models": [] }""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task FailedBackgroundRefreshKeepsTheCurrentListAndLogsWithoutTheBody(HttpStatusCode status, string body)
    {
        var handler = new RoutingHandler(_ => Json(body, status));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "secret-soniox-key";
        var cached = Catalog("us") with { FetchedAt = RefreshNow.AddDays(-2) };
        host.SetSetting("catalog", cached);

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        Assert.Equal(cached.FetchedAt, sut.CurrentCatalog!.FetchedAt);
        Assert.Equal(cached.FetchedAt, host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
        Assert.Equal("stt-rt-v6", sut.EffectiveRealtimeModelId);
        Assert.Equal(["Daniel", "Nina"], sut.AvailableVoices.Select(voice => voice.Id));
        Assert.Contains(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
        Assert.DoesNotContain(host.Logs, entry => entry.Message.Contains("secret-soniox-key", StringComparison.Ordinal)
            || entry.Message.Contains("upstream down", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackgroundRefreshKeepsTheCachedVoicesWhenTheVoiceListComesBackEmpty()
    {
        var handler = new RoutingHandler(request =>
            Json(request.RequestUri!.AbsolutePath == "/v1/models" ? V7ModelsJson : """{ "models": [] }"""));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddDays(-2) });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal(["Daniel", "Nina"], sut.AvailableVoices.Select(voice => voice.Id));
        Assert.Equal(RefreshNow, host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    [Fact]
    public async Task BackgroundRefreshKeepsSavedChoicesThatTheNewListLacks()
    {
        var voices = OneVoiceJson;
        var handler = new RoutingHandler(request =>
            Json(request.RequestUri!.AbsolutePath == "/v1/models" ? V7ModelsJson : voices));
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = RefreshNow.AddDays(-2) });
        host.SetSetting("model", "v5");
        host.SetSetting("speech-voice", "Nina");

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        // The new list lacks Nina and v5. A listed voice is used meanwhile; the pinned version is resolved when it is
        // read, so the refresh changes neither its saved value nor the model that is sent.
        Assert.Equal("Daniel", sut.SelectedVoiceId);
        Assert.Equal("Nina", host.GetSetting<string>("speech-voice"));
        Assert.Equal("v5", host.GetSetting<string>("model"));
        Assert.Equal("stt-rt-v5", sut.EffectiveRealtimeModelId);

        // Once a list offers the voice again, it applies again.
        voices = TtsModelsJson;
        await sut.ExecuteSettingsActionAsync("refresh-models", default);
        Assert.Equal("Nina", sut.SelectedVoiceId);
    }

    [Fact]
    public async Task RefreshModels_ReplacesARunningBackgroundRefresh()
    {
        var modelCalls = 0;
        var handler = new GatedHandler(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? Json(Interlocked.Increment(ref modelCalls) == 1 ? ModelsJson : V7ModelsJson)
            : Json(TtsModelsJson), blockFirstRequestOnly: true);
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";

        using var sut = new SonioxPlugin(new HttpClient(handler), time: new ManualTime(RefreshNow), backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await handler.Started;

        var button = sut.ExecuteSettingsActionAsync("refresh-models", default);
        handler.Release();
        await button;
        await sut.LastCleanupTask;

        // The background answer arrived after the button cancelled it and was dropped.
        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal("stt-rt-v7", host.GetSetting<SonioxCatalog>("catalog")!.SttModels[0].Id);
        Assert.Equal(RefreshNow, host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    [Fact]
    public async Task HourlyRecheck_ReloadsAListThatAgesWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = new ConcurrentQueue<string>();
        var host = HostWithList(RefreshNow);

        using var sut = new SonioxPlugin(
            new HttpClient(ListHandler(requests, failing: () => false)), time: clock, backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;
        Assert.Empty(requests);

        clock.Advance(TimeSpan.FromHours(24));
        await sut.LastCleanupTask;

        Assert.Equal(["/v1/models", "/v1/tts-models"], requests);
        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal(RefreshNow.AddHours(24), host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
        Assert.Equal(1, host.NotifyCount);

        // The reloaded list is fresh, so the next check makes no request.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.LastCleanupTask;
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task HourlyRecheck_StopsWhenThePluginIsDeactivated()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = 0;
        var handler = new RoutingHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });

        using var sut = new SonioxPlugin(new HttpClient(handler), time: clock, backgroundCatalogRefresh: true);
        await sut.ActivateAsync(HostWithList(RefreshNow));
        Assert.Equal(1, clock.ActiveTimers);
        var lastRefresh = sut.CatalogRefreshTask;

        await sut.DeactivateAsync();
        clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal(0, clock.ActiveTimers);
        Assert.Same(lastRefresh, sut.CatalogRefreshTask);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Dispose_StopsTheHourlyRecheck()
    {
        var clock = new RecheckTestClock(RefreshNow);
        using var sut = new SonioxPlugin(new HttpClient(new FailingHandler()), time: clock, backgroundCatalogRefresh: true);
        await sut.ActivateAsync(HostWithList(RefreshNow));
        Assert.Equal(1, clock.ActiveTimers);

        sut.Dispose();

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task HourlyRecheck_RetriesAFailedRefresh()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var fail = true;
        var requests = new ConcurrentQueue<string>();
        var host = HostWithList(RefreshNow.AddDays(-2));

        using var sut = new SonioxPlugin(
            new HttpClient(ListHandler(requests, failing: () => Volatile.Read(ref fail))), time: clock,
            backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;
        Assert.Equal(["/v1/models"], requests);

        // One interval is longer than the ten-minute wait after a failure, so the next check tries again.
        Volatile.Write(ref fail, false);
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.LastCleanupTask;

        Assert.Equal(["/v1/models", "/v1/models", "/v1/tts-models"], requests);
        Assert.Equal("stt-rt-v7", sut.EffectiveRealtimeModelId);
        Assert.Equal(
            RefreshNow + ModelCatalogAutoRefresh.RecheckInterval,
            host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    [Fact]
    public async Task HourlyRecheck_KeepsTheTenMinuteWaitAfterAFailure()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var fail = true;
        var requests = new ConcurrentQueue<string>();
        var host = HostWithList(RefreshNow.AddDays(-2));

        using var sut = new SonioxPlugin(
            new HttpClient(ListHandler(requests, failing: () => Volatile.Read(ref fail))), time: clock,
            backgroundCatalogRefresh: true);
        await sut.ActivateAsync(host);
        await sut.LastCleanupTask;

        // A dictation five minutes before the first check fails as well, so that check falls into the wait.
        clock.Advance(TimeSpan.FromMinutes(55));
        sut.RefreshCatalogInBackgroundIfStale();
        await sut.LastCleanupTask;
        clock.Advance(TimeSpan.FromMinutes(5));
        await sut.LastCleanupTask;
        Assert.Equal(["/v1/models", "/v1/models"], requests);

        // The check an hour later comes after the wait and loads the list.
        Volatile.Write(ref fail, false);
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.LastCleanupTask;

        Assert.Equal(["/v1/models", "/v1/models", "/v1/models", "/v1/tts-models"], requests);
        Assert.Equal(RefreshNow.AddHours(2), host.GetSetting<SonioxCatalog>("catalog")!.FetchedAt);
    }

    private static FeatureHost HostWithList(DateTimeOffset fetchedAt)
    {
        var host = new FeatureHost();
        host.Secrets["api-key"] = "key";
        host.SetSetting("catalog", Catalog("us") with { FetchedAt = fetchedAt });
        return host;
    }

    /// <summary>Answers the model and voice lists, or fails with HTTP 502 while <paramref name="failing"/> returns true.</summary>
    private static RoutingHandler ListHandler(ConcurrentQueue<string> requests, Func<bool> failing) => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        requests.Enqueue(path);
        if (failing())
            return Json("""{ "error_message": "upstream down" }""", HttpStatusCode.BadGateway);

        return path switch
        {
            "/v1/models" => Json(V7ModelsJson),
            "/v1/tts-models" => Json(TtsModelsJson),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        };
    });

    /// <summary>
    /// Holds requests until released and ignores cancellation, like a slow server. With <c>blockFirstRequestOnly</c>,
    /// later requests answer right away.
    /// </summary>
    private sealed class GatedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, bool blockFirstRequestOnly = false)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _requests = new();
        private int _count;

        public Task Started => _started.Task;
        public IReadOnlyList<string> Requests => _requests.ToArray();
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue($"{request.Headers.Authorization?.Parameter} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath}");
            if (Interlocked.Increment(ref _count) == 1 || !blockFirstRequestOnly)
            {
                _started.TrySetResult();
                await _release.Task;
            }

            return respond(request);
        }
    }
}
