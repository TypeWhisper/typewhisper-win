using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed partial class ProviderTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] LiveTextModels =
        ["mistral-large-4", "mistral-medium-2604", "mistral-small-2603", "ministral-14b-2512", "future-chat-2701"];
    private static readonly string[] CatalogResponseTextModels = ["codestral-latest", "mistral-small-latest"];

    [Fact]
    public void PublicConstructorKeepsTheBackgroundRefreshOffInTestProcesses()
    {
        // The test project sets this switch, so the package test, which loads the built plugin through its public
        // constructor, cannot reach Mistral.
        Assert.False(TypeWhisper.Plugin.Shared.ModelCatalogAutoRefresh.AllowedInThisProcess);
        using var plugin = new VoxtralPlugin();
        Assert.False(plugin.RefreshesModelsAutomatically);
        using var optedIn = new VoxtralPlugin(OfflineHttpClient(), refreshModelsAutomatically: true);
        Assert.True(optedIn.RefreshesModelsAutomatically);
    }

    [Fact]
    public async Task ActivationRefreshesADayOldCatalogInTheBackgroundAndSavesTheDate()
    {
        var clock = new TestClock(RefreshNow);
        var requests = 0;
        string? url = null, authorization = null;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            url = request.RequestUri!.AbsoluteUri;
            authorization = request.Headers.Authorization?.ToString();
            return Json(LiveCatalog);
        }));
        var host = HostWithCatalog("fixture-key", CatalogResponse, clock.Now - TimeSpan.FromHours(24));

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(1, requests);
        Assert.Equal("https://api.mistral.ai/v1/models", url);
        Assert.Equal("Bearer fixture-key", authorization);
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(MiniTranscribe, plugin.SelectedModelId);
        Assert.Equal(clock.Now, SavedCatalog(host).FetchedAtUtc);
        Assert.False(plugin.IsModelCatalogStale);
    }

    [Fact]
    public async Task ActivationKeepsAnUndatedCatalogFromEarlierVersionsUntilTheRefreshDelivers()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new GatedHandler(_ => LiveCatalog);
        var host = HostWithCatalog("fixture-key", CatalogResponse, fetchedAt: null);

        using var plugin = new VoxtralPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await handler.Started;

        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));

        handler.Release();
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(clock.Now, SavedCatalog(host).FetchedAtUtc);
    }

    [Fact]
    public async Task ActivationKeepsACatalogYoungerThanADayWithoutRequests()
    {
        var clock = new TestClock(RefreshNow);
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        }));
        var host = HostWithCatalog("fixture-key", CatalogResponse, clock.Now.AddHours(-23));

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(0, requests);
        Assert.False(plugin.IsModelCatalogStale);
        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(plugin.IsModelCatalogStale);
    }

    [Theory]
    [InlineData("\"not a date\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("\"2026-11-09T13:00:00+00:00\"")]
    public async Task DamagedOrFutureFetchDateCountsAsStaleAndKeepsTheCatalog(string savedDate)
    {
        var host = HostWithCatalog("fixture-key", CatalogResponse, RefreshNow);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(host.Settings["configuration"].GetProperty("Values"))!;
        var catalog = JsonNode.Parse(values["modelCatalog"])!;
        catalog["FetchedAtUtc"] = JsonNode.Parse(savedDate);
        values["modelCatalog"] = catalog.ToJsonString();
        host.Settings["configuration"] = JsonSerializer.SerializeToElement(new { SecretName = "api-key-fixture", Values = values });

        using var plugin = OfflinePlugin(new TestClock(RefreshNow));
        await plugin.ActivateAsync(host);

        Assert.True(plugin.IsModelCatalogStale);
        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ActivationDoesNotRefreshWithoutApiKey()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        }));

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true);
        await plugin.ActivateAsync(new Host());

        Assert.True(plugin.ModelCatalogRefreshTask.IsCompleted);
        await plugin.ModelCatalogRefreshTask;
        Assert.Equal(0, requests);
        Assert.Equal(DefaultTextModels, plugin.SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ModelRefreshFinishingAfterDeactivationChangesNothing()
    {
        var handler = new GatedHandler(_ => LiveCatalog);
        var host = HostWithCatalog("fixture-key", catalogResponse: null, fetchedAt: null);
        var before = JsonSerializer.Serialize(host.Settings);

        using var plugin = new VoxtralPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await plugin.ActivateAsync(host);
        await handler.Started;
        await plugin.DeactivateAsync();
        handler.Release();
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.DoesNotContain(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task KeyChangeDiscardsTheOldAccountsCatalogAndRefreshesWithTheNewKey()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new GatedHandler(key => key == "second-key" ? LiveCatalog : throw new InvalidOperationException("Unexpected key."));
        var host = HostWithCatalog("first-key", CatalogResponse, clock.Now.AddHours(-1));

        using var plugin = new VoxtralPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));

        await plugin.SetApiKeyAsync("second-key");
        await handler.Started;

        // The first account's list is gone before the second account's list arrives.
        Assert.Equal(DefaultTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Null(SavedCatalog(host).FetchedAtUtc);

        handler.Release();
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(["second-key"], handler.Keys);
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(clock.Now, SavedCatalog(host).FetchedAtUtc);
    }

    [Fact]
    public async Task KeyChangeDropsTheResultOfARefreshStartedWithTheOldKey()
    {
        var handler = new GatedHandler(key => key == "first-key" ? CatalogResponse : LiveCatalog);
        var host = HostWithCatalog("first-key", catalogResponse: null, fetchedAt: null);

        using var plugin = new VoxtralPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await plugin.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = plugin.ModelCatalogRefreshTask;

        await plugin.SetApiKeyAsync("second-key");
        handler.Release();
        await firstRefresh;
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(["first-key", "second-key"], handler.Keys.Order());
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(LiveTextModels, SavedCatalog(host).Text.Select(m => m.Id));
        Assert.DoesNotContain(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task ClearingTheKeyStartsNoRefresh()
    {
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        }));
        var clock = new TestClock(RefreshNow);
        var host = HostWithCatalog("fixture-key", CatalogResponse, clock.Now.AddHours(-1));

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("");
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(0, requests);
        Assert.False(plugin.IsConfigured);
    }

    [Theory]
    [InlineData(503, """{"message":"overloaded"}""")]
    [InlineData(200, """{"data":[{"id":"incomplete"}]}""")]
    [InlineData(200, "not-json")]
    public async Task FailedBackgroundRefreshKeepsTheCurrentCatalogAndLogsWithoutTheKey(int status, string body)
    {
        var clock = new TestClock(RefreshNow);
        using var http = new HttpClient(new Handler((_, _) =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) }));
        var fetchedAt = clock.Now.AddDays(-2);
        var host = HostWithCatalog("secret-mistral-key", CatalogResponse, fetchedAt);
        var before = JsonSerializer.Serialize(host.Settings);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(fetchedAt, SavedCatalog(host).FetchedAtUtc);
        Assert.Contains(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
        Assert.DoesNotContain(host.Logs, entry => entry.Message.Contains("secret-mistral-key", StringComparison.Ordinal)
            || entry.Message.Contains("overloaded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"data":[]}""")]
    [InlineData("""{"data":[{"id":"mistral-small-2603","capabilities":{"completion_chat":true}}]}""")]
    [InlineData("""{"data":[{"id":"voxtral-mini-transcribe-3-0","capabilities":{"audio_transcription":true}}]}""")]
    public async Task BackgroundRefreshKeepsTheCatalogWhenTheNewListWouldSwitchOffACapability(string response)
    {
        var clock = new TestClock(RefreshNow);
        using var http = new HttpClient(new Handler((_, _) => Json(response)));
        var host = HostWithCatalog("fixture-key", CatalogResponse, clock.Now.AddDays(-2));
        var before = JsonSerializer.Serialize(host.Settings);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(before, JsonSerializer.Serialize(host.Settings));
        Assert.True(plugin.IsAvailable);
        Assert.True(((ITranscriptionEnginePlugin)plugin).IsConfigured);
        Assert.Contains(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task BackgroundRefreshKeepsSavedModelsThatTheNewListLacks()
    {
        var clock = new TestClock(RefreshNow);
        var response = """
            {"data":[
              {"id":"voxtral-mini-transcribe-3-0","capabilities":{"audio_transcription":true}},
              {"id":"mistral-small-2603","capabilities":{"completion_chat":true}}
            ]}
            """;
        using var http = new HttpClient(new Handler((_, _) => Json(response)));
        var host = HostWithCatalog("fixture-key", LiveCatalog, clock.Now.AddDays(-1),
            new() { ["model"] = "voxtral-small-transcribe-3-0", ["llmModel"] = "ministral-14b-2512" });

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        Assert.Equal("voxtral-small-transcribe-3-0", plugin.SelectedModelId);
        await plugin.ModelCatalogRefreshTask;

        // The new list lacks both saved models: the defaults apply, and the saved values stay.
        Assert.Equal(MiniTranscribe, plugin.SelectedModelId);
        Assert.Equal("mistral-small-2603", plugin.TextSettings.Single(s => s.Id == "llmModel").Value);
        var values = host.Settings["configuration"].GetProperty("Values");
        Assert.Equal("voxtral-small-transcribe-3-0", values.GetProperty("model").GetString());
        Assert.Equal("ministral-14b-2512", values.GetProperty("llmModel").GetString());

        // Once a list offers them again, they apply again.
        response = LiveCatalog;
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.Equal("voxtral-small-transcribe-3-0", plugin.SelectedModelId);
        Assert.Equal("ministral-14b-2512", plugin.TextSettings.Single(s => s.Id == "llmModel").Value);
    }

    [Fact]
    public async Task RefreshModelsActionRecordsTheFetchDateFromTheClock()
    {
        var clock = new TestClock(RefreshNow);
        using var http = new HttpClient(new Handler((_, _) => Json(LiveCatalog)));
        var host = new Host();

        using var plugin = new VoxtralPlugin(http, clock: clock);
        await plugin.ActivateAsync(host); await Configure(plugin);
        Assert.True(plugin.IsModelCatalogStale);

        await plugin.ExecuteSettingsActionAsync("refreshModels", default);

        Assert.Equal(clock.Now, SavedCatalog(host).FetchedAtUtc);
        Assert.False(plugin.IsModelCatalogStale);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(plugin.IsModelCatalogStale);
    }

    [Fact]
    public async Task RefreshModelsActionReplacesARunningBackgroundRefresh()
    {
        var handler = new GatedHandler(_ => CatalogResponse, blockFirstRequestOnly: true, laterJson: LiveCatalog);
        var host = HostWithCatalog("fixture-key", catalogResponse: null, fetchedAt: null);

        using var plugin = new VoxtralPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await plugin.ActivateAsync(host);
        await handler.Started;
        var background = plugin.ModelCatalogRefreshTask;

        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        handler.Release();
        await background;

        // The background result arrived after the button's and was dropped.
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(LiveTextModels, SavedCatalog(host).Text.Select(m => m.Id));
    }

    private const string ModelsUrl = "https://api.mistral.ai/v1/models";

    [Fact]
    public async Task HourlyRecheckReloadsACatalogThatAgesWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Enqueue(url);
            return url == ModelsUrl ? Json(LiveCatalog) : throw new InvalidOperationException($"Unexpected request: {url}");
        }));
        var host = HostWithCatalog("fixture-key", CatalogResponse, RefreshNow);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;
        Assert.Empty(requests);
        Assert.Equal(CatalogResponseTextModels, plugin.SupportedModels.Select(m => m.Id));

        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal([ModelsUrl], requests);
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(LiveTextModels, SavedCatalog(host).Text.Select(m => m.Id));
        Assert.Equal(RefreshNow.AddHours(24), SavedCatalog(host).FetchedAtUtc);

        // The reloaded list is fresh, so the next check makes no request.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await plugin.ModelCatalogRefreshTask;
        Assert.Single(requests);
    }

    [Fact]
    public async Task HourlyRecheckStopsWhenThePluginIsDeactivated()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        }));
        var host = HostWithCatalog("fixture-key", CatalogResponse, RefreshNow);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        Assert.Equal(1, clock.ActiveTimers);
        var lastRefresh = plugin.ModelCatalogRefreshTask;

        await plugin.DeactivateAsync();
        clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal(0, clock.ActiveTimers);
        Assert.Same(lastRefresh, plugin.ModelCatalogRefreshTask);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task HourlyRecheckRetriesAFailedRefresh()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri != ModelsUrl)
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            // The refresh at start fails, as on a PC that starts offline; the next one succeeds.
            return Interlocked.Increment(ref requests) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
                : Json(LiveCatalog);
        }));
        var fetchedAt = RefreshNow.AddDays(-2);
        var host = HostWithCatalog("fixture-key", CatalogResponse, fetchedAt);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        await plugin.ModelCatalogRefreshTask;
        Assert.Equal(1, requests);
        Assert.Equal(fetchedAt, SavedCatalog(host).FetchedAtUtc);

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(2, requests);
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(RefreshNow + ModelCatalogAutoRefresh.RecheckInterval, SavedCatalog(host).FetchedAtUtc);
    }

    [Fact]
    public async Task HourlyRecheckNeverRefreshesWithAKeyThatIsBeingReplaced()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var keys = new ConcurrentQueue<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            keys.Enqueue(request.Headers.Authorization!.Parameter!);
            return Json(LiveCatalog);
        }));
        var host = HostWithCatalog("first-key", CatalogResponse, RefreshNow);

        using var plugin = new VoxtralPlugin(http, refreshModelsAutomatically: true, clock: clock);
        await plugin.ActivateAsync(host);
        var beforeKeyChange = plugin.ModelCatalogRefreshTask;
        var secretStore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StoreSecretDelay = secretStore.Task;

        // The key change waits for the secret store. The first list is a day old by then, so a check that fires
        // meanwhile and ignored the key change would refresh it with the first key.
        var keyChange = plugin.SetApiKeyAsync("second-key");
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Same(beforeKeyChange, plugin.ModelCatalogRefreshTask);
        secretStore.SetResult();
        await keyChange;
        await plugin.ModelCatalogRefreshTask;

        Assert.Equal(["second-key"], keys);
        Assert.Equal(LiveTextModels, plugin.SupportedModels.Select(m => m.Id));
    }

    private static VoxtralPlugin OfflinePlugin(TimeProvider? clock = null) => new(OfflineHttpClient(), clock: clock);

    private static HttpClient OfflineHttpClient() =>
        new(new Handler((request, _) => throw new InvalidOperationException($"Unexpected network request: {request.RequestUri}")));

    /// <summary>A host with a saved key and, when given, a catalog parsed from a <c>/v1/models</c> response.</summary>
    private static Host HostWithCatalog(string key, string? catalogResponse, DateTimeOffset? fetchedAt,
        Dictionary<string, string>? values = null)
    {
        var host = new Host();
        var saved = new Dictionary<string, string>(values ?? []);
        if (catalogResponse is not null)
        {
            using var document = JsonDocument.Parse(catalogResponse);
            saved["modelCatalog"] = JsonSerializer.Serialize(MistralCatalog.Parse(document.RootElement) with { FetchedAtUtc = fetchedAt });
        }
        host.Secrets["api-key-fixture"] = key;
        host.Settings["configuration"] = JsonSerializer.SerializeToElement(new { SecretName = "api-key-fixture", Values = saved });
        return host;
    }

    private static MistralModelCatalog SavedCatalog(Host host) =>
        MistralCatalog.Load(host.Settings["configuration"].GetProperty("Values").TryGetProperty("modelCatalog", out var saved)
            ? saved.GetString()!
            : "");

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// Holds model list requests until released and ignores cancellation, like a slow server. With
    /// <c>blockFirstRequestOnly</c>, later requests answer right away with <c>laterJson</c>.
    /// </summary>
    private sealed class GatedHandler(Func<string, string> jsonForKey, bool blockFirstRequestOnly = false, string? laterJson = null)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _keys = new();
        private int _requests;

        public Task Started => _started.Task;
        public IReadOnlyList<string> Keys => _keys.ToArray();
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.Authorization!.Parameter!;
            _keys.Enqueue(key);
            if (Interlocked.Increment(ref _requests) > 1 && blockFirstRequestOnly)
                return Json(laterJson!);

            _started.TrySetResult();
            await _release.Task;
            return Json(jsonForKey(key));
        }
    }
}
