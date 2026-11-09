using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using TypeWhisper.Plugin.Xai;

namespace TypeWhisper.PluginSystem.Tests;

public partial class XaiPluginTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string RefreshedModels = """{ "data": [ { "id": "grok-5", "owned_by": "xai" }, { "id": "grok-4.3", "owned_by": "xai" } ] }""";
    private const string RefreshedVoices = """{ "voices": [ { "voice_id": "ara", "name": "Ara" }, { "voice_id": "eve", "name": "Eve" } ] }""";

    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    public async Task Activation_RefreshesUndatedOrDayOldListsAndSavesTheirDates(int? hoursOld)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            Assert.Equal("Bearer xai-key", request.Headers.Authorization?.ToString());
            await release.Task;
            return CatalogResponse(request, RefreshedModels, RefreshedVoices);
        });
        var fetchedAt = hoursOld is { } hours ? RefreshNow.AddHours(-hours) : (DateTimeOffset?)null;
        var host = CatalogHost("xai-key", fetchedAt, fetchedAt);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        // The saved lists stay in use until the refresh delivers new ones.
        Assert.Equal(["grok-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["saved-voice"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        release.SetResult();
        await sut.CatalogRefreshTask;

        Assert.Equal(["https://api.x.ai/v1/models", "https://api.x.ai/v1/tts/voices"], handler.Paths);
        Assert.Equal(["grok-4.3", "grok-5"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["ara", "eve"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal(RefreshNow, sut.FetchedLlmModelsAt);
        Assert.Equal(RefreshNow, sut.FetchedVoicesAt);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedVoicesAtUtc"));
        Assert.Equal(Fingerprint("xai-key"), host.GetSetting<string>("llmCatalogKeyFingerprint"));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task Activation_RefreshesOnlyTheListThatIsDue()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedModels, RefreshedVoices)));
        var host = CatalogHost("xai-key", RefreshNow.AddHours(-23), RefreshNow.AddDays(-2));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(["https://api.x.ai/v1/tts/voices"], handler.Paths);
        Assert.Equal(["grok-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["ara", "eve"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal(RefreshNow.AddHours(-23), sut.FetchedLlmModelsAt);
    }

    [Fact]
    public async Task Activation_KeepsListsYoungerThanADayWithoutRequests()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected catalog refresh."));
        var host = CatalogHost("xai-key", RefreshNow.AddHours(-23), RefreshNow.AddHours(-1));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Empty(handler.Paths);
        Assert.Equal(["grok-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Activation_WithoutApiKeyDoesNotRefresh()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected catalog refresh."));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.CatalogRefreshTask;

        Assert.Empty(handler.Paths);
        Assert.Equal(XaiPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task DamagedFetchDateReadsAsNoDateAndRefreshes()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedModels, RefreshedVoices)));
        var host = CatalogHost("xai-key", RefreshNow, RefreshNow);
        host.SetSetting("fetchedLlmModelsAtUtc", "not a date");
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(["https://api.x.ai/v1/models"], handler.Paths);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, like a slow server whose answer arrives after deactivation.
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, RefreshedModels, RefreshedVoices);
        });
        var host = CatalogHost("xai-key", null, null);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.CatalogRefreshTask;

        Assert.Null(host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
        Assert.Null(host.GetSetting<DateTimeOffset?>("fetchedVoicesAtUtc"));
        Assert.Equal("grok-saved", Assert.Single(host.GetSetting<List<XaiFetchedModel>>("fetchedLlmModels")!).Id);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task KeyChange_DropsTheOldAccountsListsAndRefreshesWithTheNewKey()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            if (request.Headers.Authorization?.Parameter == "first-key")
            {
                await releaseFirst.Task;
                return CatalogResponse(request, """{ "data": [ { "id": "grok-first-only" } ] }""",
                    """{ "voices": [ { "voice_id": "first-voice" } ] }""");
            }

            Assert.Equal("Bearer second-key", request.Headers.Authorization?.ToString());
            await releaseSecond.Task;
            return CatalogResponse(request, RefreshedModels, RefreshedVoices);
        });
        var host = CatalogHost("first-key", null, null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.CatalogRefreshTask;

        await sut.SetApiKeyAsync("second-key");

        // The first account's lists are gone before the new ones arrive.
        Assert.Empty(sut.FetchedLlmModels);
        Assert.Empty(sut.FetchedVoices);
        Assert.Null(sut.FetchedLlmModelsAt);
        releaseSecond.SetResult();
        await sut.CatalogRefreshTask;
        // The refresh started with the first key answers last and is dropped.
        releaseFirst.SetResult();
        await firstRefresh;

        Assert.Equal(["grok-4.3", "grok-5"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["ara", "eve"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal(Fingerprint("second-key"), host.GetSetting<string>("llmCatalogKeyFingerprint"));
        Assert.Equal(Fingerprint("second-key"), host.GetSetting<string>("voiceCatalogKeyFingerprint"));
        using var reopened = CreateOfflinePlugin();
        await reopened.ActivateAsync(host);
        Assert.Equal(["grok-4.3", "grok-5"], reopened.SupportedModels.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task ManualRefreshStartedBeforeAKeyChangeIsNotSavedUnderTheNewKey()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, """{ "data": [ { "id": "grok-first-only" } ] }""", RefreshedVoices);
        });
        var host = CatalogHost("first-key", RefreshNow, RefreshNow);
        using var sut = new XaiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        var refresh = sut.ExecuteSettingsActionAsync("models", CancellationToken.None);
        await handler.Started;
        await sut.SetApiKeyAsync("second-key");
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "grok-first-only");
        using var reopened = CreateOfflinePlugin();
        await reopened.ActivateAsync(host);
        Assert.DoesNotContain(reopened.SupportedModels, model => model.Id == "grok-first-only");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "{}")]
    [InlineData(HttpStatusCode.OK, """{ "data": [] }""", """{ "voices": [] }""")]
    [InlineData(HttpStatusCode.OK, """{ "data": [ { "id": "grok-imagine-image" } ] }""", """{ "voices": [ { "voice_id": " " } ] }""")]
    public async Task FailedOrEmptyRefreshKeepsTheCurrentLists(HttpStatusCode status, string models, string voices)
    {
        var dayOld = RefreshNow.AddDays(-1);
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, models, voices, status)));
        var host = CatalogHost("xai-key", dayOld, dayOld);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(2, handler.Paths.Count);
        Assert.Equal(["grok-saved"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["saved-voice"], sut.AvailableVoices.Select(v => v.Id).ToArray());
        Assert.Equal(dayOld, host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
        Assert.Equal(dayOld, host.GetSetting<DateTimeOffset?>("fetchedVoicesAtUtc"));
        Assert.Equal(2, host.Warnings.Count);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SavedModelAndVoiceSurviveListsThatLackThemAndApplyAgainOnceOffered()
    {
        var models = """{ "data": [ { "id": "grok-5" }, { "id": "grok-4.3" }, { "id": "grok-3-mini" } ] }""";
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, models, RefreshedVoices)));
        var host = CatalogHost("xai-key", null, null);
        host.SetSetting("selectedLlmModel", "grok-saved");
        host.SetSetting("selectedVoice", "saved-voice");
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        Assert.Equal("grok-saved", sut.SelectedLlmModelId);
        await sut.CatalogRefreshTask;

        // Grok 4.3 and Eve stand in, not the alphabetically first entries; the saved values stay.
        Assert.Equal(XaiPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal("eve", sut.SelectedVoiceId);
        Assert.Equal("grok-saved", host.GetSetting<string>("selectedLlmModel"));
        Assert.Equal("saved-voice", host.GetSetting<string>("selectedVoice"));

        models = """{ "data": [ { "id": "grok-saved" }, { "id": "grok-4.3" } ] }""";
        await sut.ExecuteSettingsActionAsync("models", CancellationToken.None);

        Assert.Equal("grok-saved", sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task RefreshButtonsRecordTheirListsFetchDate()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedModels, RefreshedVoices)));
        var dayOld = RefreshNow.AddDays(-1);
        var host = CatalogHost("xai-key", dayOld, dayOld);
        using var sut = new XaiPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        Assert.Equal("Updated.", await sut.ExecuteSettingsActionAsync("models", CancellationToken.None));

        Assert.Equal(RefreshNow, sut.FetchedLlmModelsAt);
        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedLlmModelsAtUtc"));
        Assert.Equal(dayOld, host.GetSetting<DateTimeOffset?>("fetchedVoicesAtUtc"));

        await sut.ExecuteSettingsActionAsync("voices", CancellationToken.None);

        Assert.Equal(RefreshNow, host.GetSetting<DateTimeOffset?>("fetchedVoicesAtUtc"));
        Assert.Equal(["https://api.x.ai/v1/models", "https://api.x.ai/v1/tts/voices"], handler.Paths);
    }

    private static XaiPlugin CreateRefreshingPlugin(HttpMessageHandler handler) =>
        new(new HttpClient(handler), refreshCatalogsAutomatically: true, clock: new TestClock(RefreshNow));

    private static TestPluginHostServices CatalogHost(string? apiKey, DateTimeOffset? modelsFetchedAt, DateTimeOffset? voicesFetchedAt)
    {
        var host = new TestPluginHostServices();
        if (apiKey is not null)
            host.Secrets["api-key"] = apiKey;
        host.SetSetting("fetchedLlmModels", new List<XaiFetchedModel> { new("grok-saved", "xai") });
        host.SetSetting("llmCatalogKeyFingerprint", Fingerprint(apiKey));
        host.SetSetting("fetchedVoices", new List<XaiFetchedVoice> { new("saved-voice", "Saved voice", "en") });
        host.SetSetting("voiceCatalogKeyFingerprint", Fingerprint(apiKey));
        if (modelsFetchedAt is not null)
            host.SetSetting("fetchedLlmModelsAtUtc", modelsFetchedAt);
        if (voicesFetchedAt is not null)
            host.SetSetting("fetchedVoicesAtUtc", voicesFetchedAt);
        return host;
    }

    private static string Fingerprint(string? apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? "")));

    private static HttpResponseMessage CatalogResponse(HttpRequestMessage request, string models, string voices,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/v1/tts/voices" ? voices : models,
                Encoding.UTF8, "application/json"),
        };

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers catalog requests and records which lists were requested.</summary>
    private sealed class CatalogHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _paths = [];

        public Task Started => _started.Task;

        public List<string> Paths
        {
            get { lock (_paths) return [.. _paths]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_paths) _paths.Add(request.RequestUri!.GetLeftPart(UriPartial.Path));
            _started.TrySetResult();
            return respond(request);
        }
    }
}
