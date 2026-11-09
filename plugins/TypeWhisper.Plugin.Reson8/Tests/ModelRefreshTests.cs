using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.Plugin.Shared;

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string RefreshedModelListJson = """
        [
          {"id":"resonant-1","displayName":"Resonant-1","endpoints":["prerecorded","realtime"],"languages":["de","en","nl"],"isDefault":true},
          {"id":"rumble-3-flash","displayName":"Rumble-3-Flash","endpoints":["prerecorded"],"languages":["de","en"],"isDefault":false}
        ]
        """;
    private static readonly string[] RefreshedModels = ["__default__", "rumble-3-flash", "custom-1"];

    [Fact]
    public async Task ActivateAsync_RefreshesDayOldListsAndSavesTheDate()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            requests.Enqueue($"{request.Headers.Authorization} {request.RequestUri}");
            return Lists(request);
        }));
        var host = ListHost(fetchedAt: Now - ModelCatalogAutoRefresh.MaximumAge);
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel("deleted-1", "Deleted", null, null) });
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal([
            "ApiKey reson-key https://api.reson8.dev/v1/model",
            "ApiKey reson-key https://api.reson8.dev/v1/custom-model"], requests);
        Assert.Equal(RefreshedModels, sut.TranscriptionModels.Select(model => model.Id));
        Assert.Equal(Now, sut.ModelListsFetchedAt);
        Assert.Equal(Now, host.GetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName));
        Assert.Equal(["resonant-1", "rumble-3-flash"], host.GetSetting<List<Reson8Model>>("fetchedModels")!.Select(model => model.Id));
        Assert.Equal("custom-1", Assert.Single(host.GetSetting<List<Reson8CustomModel>>("fetchedCustomModels")!).Id);
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ActivateAsync_KeepsUndatedListsUntilTheRefreshReplacesThem()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, request => Lists(request));
        using var http = new HttpClient(handler);
        // Reson8 1.3.x saved its lists without a date.
        var host = ListHost();
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await handler.Started;
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "rumble-2-flash");
        release.SetResult();
        await sut.ModelRefreshTask;

        Assert.Equal(RefreshedModels, sut.TranscriptionModels.Select(model => model.Id));
        Assert.Equal(Now, sut.ModelListsFetchedAt);
    }

    [Fact]
    public async Task ActivateAsync_MakesNoRequestForListsYoungerThanADay()
    {
        var host = ListHost(fetchedAt: Now.AddHours(-23));
        using var sut = CreateOfflinePlugin(refreshModels: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Contains(sut.TranscriptionModels, model => model.Id == "rumble-2-flash");
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRefreshListsWithoutApiKey()
    {
        var host = ListHost(key: null);
        using var sut = CreateOfflinePlugin(refreshModels: true, new TestClock(Now));

        await sut.ActivateAsync(host);

        Assert.True(sut.ModelRefreshTask.IsCompleted);
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelRefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, request => Lists(request));
        using var http = new HttpClient(handler);
        var host = ListHost();
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.ModelRefreshTask;

        Assert.Equal(["resonant-1", "rumble-2-flash"], host.GetSetting<List<Reson8Model>>("fetchedModels")!.Select(model => model.Id));
        Assert.Null(host.GetSetting<List<Reson8CustomModel>>("fetchedCustomModels"));
        Assert.Null(host.GetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.DoesNotContain(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetApiKeyAsync_DropsTheOldAccountsCustomModelsAndRefreshesWithTheNewKey()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new ConcurrentQueue<string?>();
        var handler = new BlockingHandler(release.Task, request =>
        {
            keys.Enqueue(request.Headers.Authorization?.Parameter);
            return Lists(request, customModels: """[{"id":"second-account","name":"Second"}]""");
        });
        using var http = new HttpClient(handler);
        var host = ListHost(key: "first-key", fetchedAt: Now.AddHours(-1));
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel("first-account", "First", null, null) });
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));
        await sut.ActivateAsync(host);
        Assert.True(sut.ModelRefreshTask.IsCompleted);

        await sut.SetApiKeyAsync("second-key");
        await handler.Started;
        // The first account's custom models are gone before the new lists arrive; Reson8's own models stay.
        Assert.Empty(sut.FetchedCustomModels);
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "rumble-2-flash");
        Assert.Null(sut.ModelListsFetchedAt);
        Assert.Null(host.GetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName));
        release.SetResult();
        await sut.ModelRefreshTask;

        Assert.Equal(["second-key", "second-key"], keys);
        Assert.Equal("second-account", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal(Now, sut.ModelListsFetchedAt);
    }

    [Fact]
    public async Task ListsFetchedWithAReplacedKeyAreDropped()
    {
        var firstAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(
            request => request.Headers.Authorization?.Parameter == "first-key" ? firstAnswer.Task : Task.CompletedTask,
            request => Lists(request, customModels: request.Headers.Authorization?.Parameter == "first-key"
                ? """[{"id":"first-account","name":"First"}]"""
                : """[{"id":"second-account","name":"Second"}]"""));
        using var http = new HttpClient(handler);
        var host = ListHost(key: "first-key");
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.ModelRefreshTask;

        await sut.SetApiKeyAsync("second-key");
        await sut.ModelRefreshTask;
        // The first key's answer arrives last.
        firstAnswer.SetResult();
        await firstRefresh;

        Assert.Equal("second-account", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal("second-account", Assert.Single(host.GetSetting<List<Reson8CustomModel>>("fetchedCustomModels")!).Id);
    }

    [Fact]
    public async Task ServerChangeDropsARefreshOfThePreviousServerAndLoadsTheNewOne()
    {
        var oldServerAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(
            request => request.RequestUri!.Host == "api.reson8.dev" ? oldServerAnswer.Task : Task.CompletedTask,
            request => request.RequestUri!.Host == "api.reson8.dev"
                ? Lists(request, customModels: """[{"id":"old-server","name":"Old"}]""")
                : Lists(request, models: """[{"id":"proxy-model","displayName":"Proxy","isDefault":true}]""", customModels: "[]"));
        using var http = new HttpClient(handler);
        var host = ListHost();
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.ModelRefreshTask;

        await sut.SaveTextSettingAsync("baseUrl", "https://proxy.example.test", default);
        await sut.ModelRefreshTask;
        // The previous server's answer arrives last.
        oldServerAnswer.SetResult();
        await firstRefresh;

        Assert.Equal(["proxy-model"], sut.FetchedModels.Select(model => model.Id));
        Assert.Empty(sut.FetchedCustomModels);
        Assert.Equal("Proxy", Assert.Single(sut.TranscriptionModels).DisplayName);
        Assert.Equal(Now, sut.ModelListsFetchedAt);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task FailedListRefreshKeepsTheListsAndLogsNeitherKeyNorBody(int status)
    {
        using var http = new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"detail":"detail for reson-key"}""", (HttpStatusCode)status)));
        var stale = Now - ModelCatalogAutoRefresh.MaximumAge;
        var host = ListHost(fetchedAt: stale);
        host.SetSetting("fetchedCustomModels", new[] { new Reson8CustomModel("custom-1", "Legal", null, null) });
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal(["__default__", "rumble-2-flash", "custom-1"], sut.TranscriptionModels.Select(model => model.Id));
        Assert.Equal(stale, sut.ModelListsFetchedAt);
        var warning = Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
        Assert.Contains($"HTTP {status}", warning);
        Assert.DoesNotContain("reson-key", warning);
        Assert.DoesNotContain("detail", warning);
    }

    [Fact]
    public async Task RefreshWithOneFailedListKeepsItsEntriesAndRecordsTheDate()
    {
        // A proxy without /v1/model still lists its custom models.
        using var http = new HttpClient(new CapturingHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/model" ? JsonResponse("{}", HttpStatusCode.NotFound) : Lists(request)));
        var host = ListHost();
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal(["resonant-1", "rumble-2-flash"], sut.FetchedModels.Select(model => model.Id));
        Assert.Equal("custom-1", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal(Now, sut.ModelListsFetchedAt);
        Assert.Contains(host.Logs, message => message.Contains("One Reson8 model list could not be refreshed (HTTP 404)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATemporarilyFailedListLeavesTheDateSoTheNextStartTriesAgain()
    {
        var failCustomModels = true;
        using var http = new HttpClient(new CapturingHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/v1/custom-model" && Volatile.Read(ref failCustomModels)
                ? JsonResponse("{}", HttpStatusCode.ServiceUnavailable)
                : Lists(request, customModels: """[{"id":"second-account","name":"Second"}]""")));
        var host = ListHost(key: "first-key", fetchedAt: Now.AddHours(-1));
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));
        await sut.ActivateAsync(host);

        // A key change empties the custom list on purpose; its reload then fails once.
        await sut.SetApiKeyAsync("second-key");
        await sut.ModelRefreshTask;
        Assert.Empty(sut.FetchedCustomModels);
        Assert.Null(sut.ModelListsFetchedAt);

        Volatile.Write(ref failCustomModels, false);
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal("second-account", Assert.Single(sut.FetchedCustomModels).Id);
        Assert.Equal(Now, sut.ModelListsFetchedAt);
    }

    [Fact]
    public async Task SavedModelSurvivesAListThatLacksItAndAppliesAgain()
    {
        var offerRumble = false;
        using var http = new HttpClient(new CapturingHandler((request, _) =>
            Lists(request, models: Volatile.Read(ref offerRumble) ? ModelListJson : RefreshedModelListJson)));
        var host = ListHost();
        host.SetSetting("selectedModel", "rumble-2-flash");
        using var sut = new Reson8Plugin(http, refreshModelsAutomatically: true, clock: new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.ModelRefreshTask;

        Assert.Equal(Reson8Plugin.DefaultModelId, sut.SelectedModelId);
        Assert.True(sut.SupportsStreaming);
        Assert.Equal("rumble-2-flash", host.GetSetting<string>("selectedModel"));

        Volatile.Write(ref offerRumble, true);
        await sut.ExecuteSettingsActionAsync("refresh", default);

        Assert.Equal("rumble-2-flash", sut.SelectedModelId);
        Assert.False(sut.SupportsStreaming);
    }

    [Fact]
    public async Task ActivateAsync_UsesTheDefaultModelWhileTheListsLackTheSavedOne()
    {
        var host = ListHost();
        host.SetSetting("selectedModel", "deleted-custom-model");
        using var sut = CreateOfflinePlugin();

        await sut.ActivateAsync(host);

        Assert.Equal(Reson8Plugin.DefaultModelId, sut.SelectedModelId);
        Assert.DoesNotContain("custom_model_id", sut.BuildQuery(null, null, null, realtime: false).ToString());
        Assert.Equal("deleted-custom-model", host.GetSetting<string>("selectedModel"));
    }

    [Fact]
    public async Task RefreshAction_RecordsTheFetchDate()
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => Lists(request)));
        var host = ListHost();
        using var sut = new Reson8Plugin(http, clock: new TestClock(Now));
        await sut.ActivateAsync(host);
        Assert.Null(sut.ModelListsFetchedAt);

        await sut.ExecuteSettingsActionAsync("refresh", default);

        Assert.Equal(Now, sut.ModelListsFetchedAt);
        Assert.Equal(Now, host.GetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName));
        Assert.Equal(RefreshedModels, sut.TranscriptionModels.Select(model => model.Id));
    }

    [Fact]
    public async Task RefreshActionDropsListsOfAServerReplacedMeanwhile()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, request => Lists(request));
        using var http = new HttpClient(handler);
        var host = ListHost();
        using var sut = new Reson8Plugin(http);
        await sut.ActivateAsync(host);

        var refresh = sut.ExecuteSettingsActionAsync("refresh", default);
        await handler.Started;
        sut.SetCustomBaseUrl("https://proxy.example.test");
        release.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Contains("connection changed", error.Message, StringComparison.Ordinal);
        Assert.Empty(sut.FetchedModels);
        Assert.Empty(sut.FetchedCustomModels);
        Assert.Null(sut.ModelListsFetchedAt);
    }

    [Fact]
    public async Task ConnectionChangesClearTheFetchDate()
    {
        var host = ListHost(fetchedAt: Now.AddHours(-1));
        using var sut = CreateOfflinePlugin();
        await sut.ActivateAsync(host);
        Assert.Equal(Now.AddHours(-1), sut.ModelListsFetchedAt);

        sut.SetCustomAuthHeader("X-Api-Key");

        Assert.Null(sut.ModelListsFetchedAt);
        Assert.Null(host.GetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName));
        Assert.Contains(sut.TranscriptionModels, model => model.Id == "rumble-2-flash");
    }

    [Fact]
    public async Task DamagedFetchDateCountsAsNoDate()
    {
        var host = ListHost();
        host.SetSetting(Reson8Plugin.ModelListsFetchedAtSettingName, "yesterday");
        using var sut = CreateOfflinePlugin();

        await sut.ActivateAsync(host);

        Assert.Null(sut.ModelListsFetchedAt);
    }

    private static HttpResponseMessage Lists(HttpRequestMessage request, string models = RefreshedModelListJson,
        string customModels = """[{"id":"custom-1","name":"Legal"}]""") =>
        JsonResponse(request.RequestUri!.AbsolutePath.EndsWith("/v1/custom-model", StringComparison.Ordinal) ? customModels : models);

    private static TestPluginHostServices ListHost(string? key = "reson-key", DateTimeOffset? fetchedAt = null)
    {
        var host = new TestPluginHostServices();
        if (key is not null) host.Secrets["api-key"] = key;
        host.SetSetting("fetchedModels", Reson8Catalog.ParseModels(ModelListJson));
        if (fetchedAt is { } at) host.SetSetting<DateTimeOffset?>(Reson8Plugin.ModelListsFetchedAtSettingName, at);
        return host;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingHandler(Func<HttpRequestMessage, Task> wait, Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingHandler(Task release, Func<HttpRequestMessage, HttpResponseMessage> respond) : this(_ => release, respond) { }

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            // Ignores cancellation, so the answer can arrive after deactivation or a connection change, like a slow server.
            await wait(request);
            return respond(request);
        }
    }
}
