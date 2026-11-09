using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Gemini;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string ModelsUrl = "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000";

    private const string RefreshedCatalogJson = """
        {"models":[
          {"name":"models/gemini-3.8-flash","displayName":"Gemini 3.8 Flash","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-flash-lite-latest","displayName":"Gemini Flash Lite Latest"},
          {"name":"models/gemini-3.6-transcribe","displayName":"Gemini 3.6 Transcribe"},
          {"name":"models/gemini-3.6-transcribe-live","displayName":"Gemini 3.6 Transcribe Live"}
        ]}
        """;

    private const string SecondAccountCatalogJson = """
        {"models":[
          {"name":"models/gemini-pro-latest","displayName":"Gemini Pro Latest"},
          {"name":"models/gemini-3.7-transcribe","displayName":"Gemini 3.7 Transcribe"}
        ]}
        """;

    [Fact]
    public void PublicConstructor_KeepsTheBackgroundRefreshOffInTestProcesses()
    {
        // The test project sets this switch, so the package test, which loads the built plugin through its
        // public constructor, cannot reach Gemini.
        Assert.False(TypeWhisper.Plugin.Shared.ModelCatalogAutoRefresh.AllowedInThisProcess);
        using var plugin = new GeminiPlugin();
        Assert.False(plugin.RefreshesModelsAutomatically);
        using var optedIn = new GeminiPlugin(OfflineHttpClient(), refreshModelsAutomatically: true);
        Assert.True(optedIn.RefreshesModelsAutomatically);
    }

    [Fact]
    public async Task ActivateAsync_RefreshesADayOldCatalogInTheBackgroundAndSavesTheDate()
    {
        var clock = new TestClock(RefreshNow);
        var requests = 0;
        string? requestedUrl = null;
        string? sentKey = null;
        var handler = new CapturingHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            requestedUrl = request.RequestUri?.ToString();
            sentKey = request.Headers.GetValues("x-goog-api-key").Single();
            return JsonResponse(RefreshedCatalogJson);
        });
        var host = HostWithCatalog("gemini-key", clock.Now - TimeSpan.FromHours(24));

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(1, requests);
        Assert.Equal(ModelsUrl, requestedUrl);
        Assert.Equal("gemini-key", sentKey);
        Assert.Equal(["gemini-flash-lite-latest", "gemini-3.8-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(["gemini-3.6-transcribe"], sut.TranscriptionModels.Select(m => m.Id));
        Assert.True(sut.SupportsStreaming);
        Assert.Equal(clock.Now, sut.ModelCatalogFetchedAt);
        Assert.Equal(clock.Now, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-3.8-flash"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!.Select(m => m.Id));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
        Assert.False(sut.IsModelCatalogStale);
    }

    [Fact]
    public async Task ActivateAsync_KeepsAnUndatedCatalogFromOlderVersionsUntilTheRefreshDelivers()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new GatedHandler(_ => RefreshedCatalogJson);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel> { new("gemini-2.5-flash", "Gemini 2.5 Flash") });

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await handler.Started;

        // Until the new catalog arrives, the saved one stays in use.
        Assert.Equal(["gemini-2.5-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Null(sut.ModelCatalogFetchedAt);

        handler.Release();
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["gemini-flash-lite-latest", "gemini-3.8-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(clock.Now, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
    }

    [Fact]
    public async Task ActivateAsync_KeepsACatalogYoungerThanADayWithoutRequests()
    {
        var clock = new TestClock(RefreshNow);
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });
        var host = HostWithCatalog("gemini-key", clock.Now.AddHours(-23));

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(0, requests);
        Assert.False(sut.IsModelCatalogStale);
        Assert.Equal(["gemini-3.7-flash"], sut.SupportedModels.Select(m => m.Id));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(sut.IsModelCatalogStale);
    }

    [Theory]
    [InlineData("\"not a date\"")]
    [InlineData("42")]
    [InlineData("\"2026-11-09T13:00:00+00:00\"")]
    public async Task ActivateAsync_TreatsADamagedOrFutureFetchDateAsStaleAndKeepsTheCatalog(string savedDate)
    {
        var host = HostWithCatalog("gemini-key", fetchedAt: null);
        host.SetRawSetting("modelCatalogFetchedAtUtc", savedDate);

        using var sut = CreateOfflinePlugin(new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        Assert.True(sut.IsModelCatalogStale);
        Assert.Equal(["gemini-3.7-flash"], sut.SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRefreshWithoutApiKey()
    {
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await sut.ActivateAsync(new TestPluginHostServices());

        Assert.True(sut.ModelCatalogRefreshTask.IsCompleted);
        await sut.ModelCatalogRefreshTask;
        Assert.Equal(0, requests);
        Assert.Equal(GeminiPlugin.DefaultModel, sut.SupportedModels[0].Id);
    }

    [Fact]
    public async Task ModelRefreshFinishingAfterDeactivationChangesNothing()
    {
        var handler = new GatedHandler(_ => RefreshedCatalogJson);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        handler.Release();
        await sut.ModelCatalogRefreshTask;

        Assert.Empty(sut.FetchedLlmModels);
        Assert.Null(sut.ModelCatalogFetchedAt);
        Assert.Null(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2"));
        Assert.Null(host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
        Assert.Equal(0, host.SetSettingCount);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SetApiKeyAsync_DiscardsTheOldAccountsCatalogAndRefreshesWithTheNewKey()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new GatedHandler(key => key == "second-key"
            ? SecondAccountCatalogJson
            : throw new InvalidOperationException("Unexpected key."));
        var host = HostWithCatalog("first-key", clock.Now.AddHours(-1));

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        Assert.Equal(["gemini-3.7-flash"], sut.SupportedModels.Select(m => m.Id));

        await sut.SetApiKeyAsync("second-key");
        await handler.Started;

        // The first account's catalog is gone before the second account's catalog arrives.
        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-flash-latest", "gemini-pro-latest"],
            sut.SupportedModels.Select(m => m.Id));
        Assert.Null(sut.ModelCatalogFetchedAt);
        Assert.Empty(host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!);

        handler.Release();
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["second-key"], handler.Keys);
        Assert.Equal(["gemini-pro-latest"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(["gemini-3.7-transcribe"], sut.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(clock.Now, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
    }

    [Fact]
    public async Task SetApiKeyAsync_DropsTheResultOfARefreshStartedWithTheOldKey()
    {
        var handler = new GatedHandler(key => key == "first-key" ? RefreshedCatalogJson : SecondAccountCatalogJson);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "first-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true);
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.ModelCatalogRefreshTask;

        await sut.SetApiKeyAsync("second-key");
        handler.Release();
        await firstRefresh;
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["first-key", "second-key"], handler.Keys.Order());
        Assert.Equal(["gemini-pro-latest"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(
            ["gemini-pro-latest"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!.Select(m => m.Id));
    }

    [Fact]
    public async Task SetApiKeyAsync_ClearingTheKeyStartsNoRefresh()
    {
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });
        var clock = new TestClock(RefreshNow);
        var host = HostWithCatalog("gemini-key", clock.Now.AddHours(-1));

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.SetApiKeyAsync("");
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(0, requests);
        Assert.Null(sut.ApiKey);
        Assert.Empty(sut.FetchedLlmModels);
    }

    [Fact]
    public async Task BackgroundRefreshFailureKeepsTheCurrentCatalogAndLogsWithoutTheKey()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new CapturingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":{"message":"overloaded"}}"""),
        });
        var fetchedAt = clock.Now.AddDays(-2);
        var host = HostWithCatalog("secret-gemini-key", fetchedAt);
        host.ResetTracking();

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["gemini-3.7-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(["gemini-3.5-transcribe"], sut.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(fetchedAt, sut.ModelCatalogFetchedAt);
        Assert.Equal(fetchedAt, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
        Assert.Equal(0, host.SetSettingCount);
        Assert.Contains(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
        Assert.DoesNotContain(host.Logs, entry => entry.Message.Contains("secret-gemini-key", StringComparison.Ordinal)
            || entry.Message.Contains("overloaded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"models":[]}""")]
    [InlineData("""{"models":[{"name":"models/gemini-3.8-flash"}]}""")]
    [InlineData("""{"models":[{"name":"models/gemini-3.6-transcribe"}]}""")]
    public async Task BackgroundRefreshKeepsTheCatalogWhenTheNewListWouldSwitchOffACapability(string json)
    {
        var clock = new TestClock(RefreshNow);
        var handler = new CapturingHandler((_, _) => JsonResponse(json));
        var fetchedAt = clock.Now.AddDays(-2);
        var host = HostWithCatalog("gemini-key", fetchedAt);
        host.ResetTracking();

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["gemini-3.7-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(["gemini-3.5-transcribe"], sut.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(fetchedAt, sut.ModelCatalogFetchedAt);
        Assert.Equal(0, host.SetSettingCount);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.Contains(host.Logs, entry => entry.Level == PluginLogLevel.Warning);
    }

    [Fact]
    public async Task BackgroundRefreshAppliesAFirstTextOnlyCatalogAndRecordsTheDate()
    {
        // An account without transcription models: the built-in transcription model never worked for it.
        var clock = new TestClock(RefreshNow);
        var handler = new CapturingHandler((_, _) => JsonResponse("""{"models":[{"name":"models/gemini-3.8-flash"}]}"""));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(["gemini-3.8-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Empty(sut.TranscriptionModels);
        Assert.Equal(RefreshNow, sut.ModelCatalogFetchedAt);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
    }

    [Fact]
    public async Task BackgroundRefreshRefusesAFirstCatalogWithoutAnyModel()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new CapturingHandler((_, _) => JsonResponse("""{"models":[]}"""));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        Assert.NotEmpty(sut.SupportedModels);
        Assert.NotEmpty(sut.TranscriptionModels);
        Assert.Null(sut.ModelCatalogFetchedAt);
    }

    [Fact]
    public async Task BackgroundRefreshKeepsSavedModelsThatTheNewListLacks()
    {
        var clock = new TestClock(RefreshNow);
        var json = RefreshedCatalogJson;
        var handler = new CapturingHandler((_, _) => JsonResponse(json));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel>
        {
            new("gemini-3.7-flash", "Gemini 3.7 Flash"),
            new("gemini-flash-lite-latest", "Gemini Flash Lite Latest"),
        });
        host.SetSetting("fetchedTranscriptionModels.v1", new List<GeminiFetchedTranscriptionModel>
        {
            new("gemini-3.5-transcribe", "Gemini 3.5 Transcribe", null),
            new("gemini-3.4-transcribe", "Gemini 3.4 Transcribe", null),
        });
        host.SetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc", clock.Now.AddDays(-1));
        host.SetSetting("selectedTranscriptionModel", "gemini-3.4-transcribe");
        host.SetSetting("llmOptions", new { Model = "gemini-3.7-flash", TemperatureMode = "providerDefault", Temperature = 0.3 });

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;

        // The new list lacks both saved models: the defaults apply, and the saved values stay.
        Assert.Equal("gemini-3.6-transcribe", sut.SelectedModelId);
        Assert.Equal("gemini-flash-lite-latest", sut.TextSettings.Single(s => s.Id == "selectedLlmModel").Value);
        Assert.Equal("gemini-3.4-transcribe", host.GetSetting<string>("selectedTranscriptionModel"));
        Assert.Equal("gemini-3.7-flash", host.GetSetting<JsonElement>("llmOptions").GetProperty("Model").GetString());

        // Once a list offers them again, they apply again.
        json = """
            {"models":[
              {"name":"models/gemini-3.7-flash"},
              {"name":"models/gemini-flash-lite-latest"},
              {"name":"models/gemini-3.4-transcribe"},
              {"name":"models/gemini-3.6-transcribe"}
            ]}
            """;
        await sut.ExecuteSettingsActionAsync("refreshModels", default);

        Assert.Equal("gemini-3.4-transcribe", sut.SelectedModelId);
        Assert.Equal("gemini-3.7-flash", sut.TextSettings.Single(s => s.Id == "selectedLlmModel").Value);
    }

    [Fact]
    public async Task RefreshModelsAction_RecordsTheFetchDateFromTheClock()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new CapturingHandler((_, _) => JsonResponse(RefreshedCatalogJson));
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), clock: clock);
        await sut.ActivateAsync(host);
        Assert.True(sut.IsModelCatalogStale);

        Assert.Equal("Models updated.", await sut.ExecuteSettingsActionAsync("refreshModels", default));

        Assert.Equal(clock.Now, sut.ModelCatalogFetchedAt);
        Assert.Equal(clock.Now, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
        Assert.False(sut.IsModelCatalogStale);
        clock.Advance(TimeSpan.FromHours(24));
        Assert.True(sut.IsModelCatalogStale);
    }

    [Fact]
    public async Task RefreshModelsAction_ReplacesARunningBackgroundRefresh()
    {
        var clock = new TestClock(RefreshNow);
        var handler = new GatedHandler(_ => RefreshedCatalogJson, blockFirstRequestOnly: true, laterJson: SecondAccountCatalogJson);
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "gemini-key";

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await handler.Started;
        var background = sut.ModelCatalogRefreshTask;

        await sut.ExecuteSettingsActionAsync("refreshModels", default);
        handler.Release();
        await background;

        // The background result arrived after the button's and was dropped.
        Assert.Equal(["gemini-pro-latest"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(
            ["gemini-pro-latest"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!.Select(m => m.Id));
    }

    [Fact]
    public async Task HourlyRecheck_ReloadsACatalogThatAgesWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = new ConcurrentQueue<string>();
        var handler = new CapturingHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            requests.Enqueue(url);
            return url == ModelsUrl
                ? JsonResponse(RefreshedCatalogJson)
                : throw new InvalidOperationException($"Unexpected request: {url}");
        });
        var host = HostWithCatalog("gemini-key", RefreshNow);

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;
        Assert.Empty(requests);

        clock.Advance(TimeSpan.FromHours(24));
        await sut.ModelCatalogRefreshTask;

        var refreshedAt = RefreshNow.AddHours(24);
        Assert.Equal([ModelsUrl], requests);
        Assert.Equal(["gemini-flash-lite-latest", "gemini-3.8-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(["gemini-3.6-transcribe"], sut.TranscriptionModels.Select(m => m.Id));
        Assert.Equal(refreshedAt, sut.ModelCatalogFetchedAt);
        Assert.Equal(refreshedAt, host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
        Assert.Equal(
            ["gemini-flash-lite-latest", "gemini-3.8-flash"],
            host.GetSetting<List<GeminiFetchedModel>>("fetchedLlmModels.v2")!.Select(m => m.Id));

        // The reloaded catalog is fresh, so the next check makes no request.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.ModelCatalogRefreshTask;
        Assert.Single(requests);
    }

    [Fact]
    public async Task HourlyRecheck_StopsWhenThePluginIsDeactivated()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = 0;
        var handler = new CapturingHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            throw new InvalidOperationException("Unexpected model refresh.");
        });
        var host = HostWithCatalog("gemini-key", RefreshNow);

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        Assert.Equal(1, clock.ActiveTimers);
        var lastRefresh = sut.ModelCatalogRefreshTask;

        await sut.DeactivateAsync();
        clock.Advance(TimeSpan.FromHours(24));

        Assert.Equal(0, clock.ActiveTimers);
        Assert.Same(lastRefresh, sut.ModelCatalogRefreshTask);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task HourlyRecheck_RetriesAFailedRefresh()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var requests = 0;
        var handler = new CapturingHandler((request, _) =>
        {
            if (request.RequestUri!.ToString() != ModelsUrl)
                throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
            // The refresh at start fails, as on a PC that starts offline; the next one succeeds.
            return Interlocked.Increment(ref requests) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(RefreshedCatalogJson);
        });
        var fetchedAt = RefreshNow.AddDays(-2);
        var host = HostWithCatalog("gemini-key", fetchedAt);

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        await sut.ModelCatalogRefreshTask;
        Assert.Equal(1, requests);
        Assert.Equal(fetchedAt, sut.ModelCatalogFetchedAt);

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.ModelCatalogRefreshTask;

        Assert.Equal(2, requests);
        Assert.Equal(["gemini-flash-lite-latest", "gemini-3.8-flash"], sut.SupportedModels.Select(m => m.Id));
        Assert.Equal(
            RefreshNow + ModelCatalogAutoRefresh.RecheckInterval,
            host.GetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc"));
    }

    [Fact]
    public async Task HourlyRecheck_NeverRefreshesWithAKeyThatIsBeingReplaced()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var keys = new ConcurrentQueue<string>();
        var handler = new CapturingHandler((request, _) =>
        {
            keys.Enqueue(request.Headers.GetValues("x-goog-api-key").Single());
            return JsonResponse(SecondAccountCatalogJson);
        });
        var host = HostWithCatalog("first-key", RefreshNow);

        using var sut = new GeminiPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        var beforeKeyChange = sut.ModelCatalogRefreshTask;
        Task? duringKeyChange = null;
        // The key change saves the emptied catalog while it holds the gate. The first catalog is a day old by then,
        // so a check that fires there and ignored the gate would refresh it with the first key.
        host.BeforeSetSetting = _ =>
        {
            host.BeforeSetSetting = null;
            clock.Advance(TimeSpan.FromHours(24));
            duringKeyChange = sut.ModelCatalogRefreshTask;
        };

        await sut.SetApiKeyAsync("second-key");
        await sut.ModelCatalogRefreshTask;

        Assert.Same(beforeKeyChange, duringKeyChange);
        Assert.Equal(["second-key"], keys);
        Assert.Equal(["gemini-pro-latest"], sut.SupportedModels.Select(m => m.Id));
    }

    private static GeminiPlugin CreateOfflinePlugin(TimeProvider? clock = null) =>
        new(OfflineHttpClient(), clock: clock);

    private static HttpClient OfflineHttpClient() =>
        new(new CapturingHandler((request, _) =>
            throw new InvalidOperationException($"Unexpected network request: {request.RequestUri}")));

    private static TestPluginHostServices HostWithCatalog(string apiKey, DateTimeOffset? fetchedAt)
    {
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = apiKey;
        host.SetSetting("fetchedLlmModels.v2", new List<GeminiFetchedModel> { new("gemini-3.7-flash", "Gemini 3.7 Flash") });
        host.SetSetting("fetchedTranscriptionModels.v1", new List<GeminiFetchedTranscriptionModel>
        {
            new("gemini-3.5-transcribe", "Gemini 3.5 Transcribe", "gemini-3.5-transcribe-live"),
        });
        if (fetchedAt is not null)
            host.SetSetting<DateTimeOffset?>("modelCatalogFetchedAtUtc", fetchedAt);
        return host;
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private long _ticks = now.UtcTicks;
        public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// Holds catalog requests until released and ignores cancellation, like a slow server. With
    /// <c>blockFirstRequestOnly</c>, later requests answer right away with <c>laterJson</c>.
    /// </summary>
    private sealed class GatedHandler(
        Func<string, string> jsonForKey,
        bool blockFirstRequestOnly = false,
        string? laterJson = null) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _keys = new();
        private int _requests;

        public Task Started => _started.Task;
        public IReadOnlyList<string> Keys => _keys.ToArray();
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("x-goog-api-key").Single();
            _keys.Enqueue(key);
            if (Interlocked.Increment(ref _requests) > 1 && blockFirstRequestOnly)
                return JsonResponse(laterJson!);

            _started.TrySetResult();
            await _release.Task;
            return JsonResponse(jsonForKey(key));
        }
    }
}
