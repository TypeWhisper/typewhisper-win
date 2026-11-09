using System.Collections.Concurrent;
using System.Net;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.Plugin.SmallestAi;

namespace TypeWhisper.PluginSystem.Tests;

public partial class SmallestAiPluginTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string StandardVoices = """{"voices":[{"voiceId":"zoe","displayName":"Zoe"},{"voiceId":"magnus","displayName":"Magnus"},{"voiceId":"aaron","displayName":"Aaron"}]}""";
    private const string ProVoices = """{"voices":[{"voiceId":"ben","displayName":"Ben"}]}""";
    private static readonly string[] RefreshedVoices = ["lightning_v3.1:aaron", "lightning_v3.1_pro:ben", "lightning_v3.1:magnus", "lightning_v3.1:zoe"];

    [Fact]
    public async Task ActivateAsync_RefreshesDayOldVoicesAndSavesTheDate()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            requests.Enqueue($"{request.Headers.Authorization} {request.RequestUri}");
            return Voices(request);
        }));
        var host = VoiceHost("smallest-key", Now - ModelCatalogAutoRefresh.MaximumAge, Voice("retired", "Retired"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal([
            "Bearer smallest-key https://api.smallest.ai/waves/v1/lightning-v3.1/get_voices",
            "Bearer smallest-key https://api.smallest.ai/waves/v1/lightning-v3.1-pro/get_voices"], requests);
        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal(Now, sut.VoicesFetchedAt);
        Assert.Equal(Now, host.GetSetting<DateTimeOffset?>(SmallestAiPlugin.VoicesFetchedAtSetting));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ActivateAsync_KeepsUndatedVoicesUntilTheRefreshReplacesThem()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, request => Voices(request));
        using var http = new HttpClient(handler);
        // Smallest AI 1.2.x cached its catalogs without a date.
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("retired", "Retired"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await handler.Started;
        Assert.Equal(["lightning_v3.1:retired"], sut.AvailableVoices.Select(v => v.Id));
        release.SetResult();
        await sut.VoiceRefreshTask;

        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal(Now, sut.VoicesFetchedAt);
    }

    [Fact]
    public async Task ActivateAsync_MakesNoRequestForVoicesYoungerThanADay()
    {
        var host = VoiceHost("smallest-key", Now.AddHours(-23), Voice("magnus", "Magnus"));
        using var sut = CreateOfflinePlugin(refreshVoices: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal(["lightning_v3.1:magnus"], sut.AvailableVoices.Select(v => v.Id));
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRefreshVoicesWithoutApiKey()
    {
        var host = VoiceHost(key: null, fetchedAt: null, Voice("magnus", "Magnus"));
        using var sut = CreateOfflinePlugin(refreshVoices: true, new TestClock(Now));

        await sut.ActivateAsync(host);

        Assert.True(sut.VoiceRefreshTask.IsCompleted);
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VoiceRefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(release.Task, request => Voices(request));
        using var http = new HttpClient(handler);
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("retired", "Retired"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.VoiceRefreshTask;

        Assert.Equal("retired", Assert.Single(host.GetSetting<SmallestAiPlugin.SpeechVoice[]>("speech-voices")!).Id);
        Assert.Null(host.GetSetting<DateTimeOffset?>(SmallestAiPlugin.VoicesFetchedAtSetting));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.DoesNotContain(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetApiKeyAsync_KeepsAFreshCatalogBecauseItIsTheSameForEveryAccount()
    {
        var host = VoiceHost("first-key", Now.AddHours(-1), Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        host.SetSetting("speech-voice", "lightning_v3.1_pro:ben");
        using var sut = CreateOfflinePlugin(refreshVoices: true, new TestClock(Now));
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("second-key");
        await sut.VoiceRefreshTask;

        Assert.Equal(["lightning_v3.1:magnus", "lightning_v3.1_pro:ben"], sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal("lightning_v3.1_pro:ben", sut.SelectedVoiceId);
        Assert.Equal(Now.AddHours(-1), sut.VoicesFetchedAt);
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetApiKeyAsync_DropsARefreshWithTheOldKeyAndReloadsAStaleCatalogWithTheNewOne()
    {
        var firstAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new BlockingHandler(
            request => request.Headers.Authorization?.Parameter == "first-key" ? firstAnswer.Task : Task.CompletedTask,
            request => request.Headers.Authorization?.Parameter == "first-key"
                ? JsonResponse("""{"voices":[{"voiceId":"old","displayName":"Old"}]}""")
                : Voices(request));
        using var http = new HttpClient(handler);
        var host = VoiceHost("first-key", fetchedAt: null, Voice("magnus", "Magnus"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.VoiceRefreshTask;

        await sut.SetApiKeyAsync("second-key");
        await sut.VoiceRefreshTask;
        // The first key's answer arrives last.
        firstAnswer.SetResult();
        await firstRefresh;

        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
        Assert.DoesNotContain(host.GetSetting<SmallestAiPlugin.SpeechVoice[]>("speech-voices")!, voice => voice.Id == "old");
        Assert.Equal(Now, sut.VoicesFetchedAt);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task FailedVoiceRefreshKeepsTheCatalogAndLogsNeitherKeyNorBody(int status)
    {
        using var http = new HttpClient(new CapturingHandler((_, _) =>
            JsonResponse("""{"error":"detail for smallest-key"}""", (HttpStatusCode)status)));
        var stale = Now - ModelCatalogAutoRefresh.MaximumAge;
        var host = VoiceHost("smallest-key", stale, Voice("magnus", "Magnus"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal(["lightning_v3.1:magnus"], sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal(stale, sut.VoicesFetchedAt);
        var warning = Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
        Assert.Contains($"HTTP {status}", warning);
        Assert.DoesNotContain("smallest-key", warning);
        Assert.DoesNotContain("detail", warning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrIncompleteVoiceRefreshKeepsTheCatalog(bool proPoolFails)
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => !proPoolFails
            ? JsonResponse("""{"voices":[]}""")
            : IsProPool(request) ? JsonResponse("{}", HttpStatusCode.ServiceUnavailable) : Voices(request)));
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal(["lightning_v3.1:magnus", "lightning_v3.1_pro:ben"], sut.AvailableVoices.Select(v => v.Id));
        Assert.Null(sut.VoicesFetchedAt);
        Assert.Null(host.GetSetting<DateTimeOffset?>(SmallestAiPlugin.VoicesFetchedAtSetting));
        Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"voices":[]}""")]
    [InlineData("""{"voices":[{"id":"ben","name":"Ben"}]}""")]
    public async Task APoolWithoutVoicesKeepsTheCachedCatalog(string proAnswer)
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => Voices(request, pro: proAnswer)));
        var host = VoiceHost("smallest-key", Now - ModelCatalogAutoRefresh.MaximumAge,
            Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        // The Pro voice stays, so spoken feedback with a voice saved there keeps working.
        Assert.Contains(sut.AvailableVoices, voice => voice.Id == "lightning_v3.1_pro:ben");
        Assert.Equal(Now - ModelCatalogAutoRefresh.MaximumAge, sut.VoicesFetchedAt);
        Assert.Single(host.Logs, message => message.Contains("could not be refreshed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recheck_ReloadsVoicesThatAgedWhileThePluginStayedActive()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.AbsolutePath);
            return Voices(request);
        }));
        var clock = new RecheckTestClock(Now);
        var host = VoiceHost("smallest-key", Now.AddHours(-1), Voice("retired", "Retired"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;
        Assert.Empty(requests);

        // The app keeps running for a day without activating the plugin again.
        clock.Advance(TimeSpan.FromHours(24));
        await sut.VoiceRefreshTask;

        var refreshedAt = Now.AddHours(24);
        Assert.Equal(["/waves/v1/lightning-v3.1/get_voices", "/waves/v1/lightning-v3.1-pro/get_voices"], requests);
        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal(refreshedAt, sut.VoicesFetchedAt);
        Assert.Equal(refreshedAt, host.GetSetting<DateTimeOffset?>(SmallestAiPlugin.VoicesFetchedAtSetting));
        Assert.Equal(RefreshedVoices, host.GetSetting<SmallestAiPlugin.SpeechVoice[]>("speech-voices")!.Select(v => $"{v.Model}:{v.Id}"));

        // The next check finds the voices fresh.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.VoiceRefreshTask;
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recheck_TriesAFailedRefreshAgainAnHourLater(bool providerError)
    {
        var failing = true;
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new CapturingHandler((request, _) =>
        {
            requests.Enqueue(request.RequestUri!.AbsolutePath);
            if (!Volatile.Read(ref failing)) return Voices(request);
            return providerError ? JsonResponse("{}", HttpStatusCode.ServiceUnavailable) : Voices(request, pro: """{"voices":[]}""");
        }));
        var clock = new RecheckTestClock(Now);
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, clock);
        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;
        // A provider error stops at the first pool; a Pro pool without voices is rejected after both.
        var failedRequests = providerError ? 1 : 2;
        Assert.Equal(failedRequests, requests.Count);
        Assert.Equal(["lightning_v3.1:magnus", "lightning_v3.1_pro:ben"], sut.AvailableVoices.Select(v => v.Id));
        Assert.Null(sut.VoicesFetchedAt);

        Volatile.Write(ref failing, false);
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.VoiceRefreshTask;

        Assert.Equal(failedRequests + 2, requests.Count);
        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
        Assert.Equal(Now + ModelCatalogAutoRefresh.RecheckInterval, sut.VoicesFetchedAt);
    }

    [Fact]
    public async Task DeactivateAsync_StopsTheRechecks()
    {
        var clock = new RecheckTestClock(Now);
        var host = VoiceHost("smallest-key", Now.AddHours(-1), Voice("magnus", "Magnus"));
        using var sut = CreateOfflinePlugin(refreshVoices: true, clock);
        await sut.ActivateAsync(host);
        Assert.Equal(1, clock.ActiveTimers);

        await sut.DeactivateAsync();
        clock.Advance(TimeSpan.FromHours(24));
        await sut.VoiceRefreshTask;

        Assert.Equal(0, clock.ActiveTimers);
        Assert.DoesNotContain(host.Logs, message => message.Contains("in the background", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_StopsTheRechecks()
    {
        var clock = new RecheckTestClock(Now);
        var sut = CreateOfflinePlugin(refreshVoices: true, clock);
        await sut.ActivateAsync(VoiceHost("smallest-key", Now.AddHours(-1), Voice("magnus", "Magnus")));
        Assert.Equal(1, clock.ActiveTimers);

        sut.Dispose();

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task TheButtonSavesAPoolWithoutVoices()
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => Voices(request, pro: """{"voices":[]}""")));
        var host = VoiceHost("smallest-key", Now.AddHours(-1), Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));
        await sut.ActivateAsync(host);

        await sut.ExecuteSettingsActionAsync("refresh-voices", default);

        Assert.DoesNotContain(sut.AvailableVoices, voice => voice.Id == "lightning_v3.1_pro:ben");
        Assert.Equal(Now, sut.VoicesFetchedAt);
    }

    [Fact]
    public async Task SavedVoiceSurvivesACatalogThatLacksItAndAppliesAgain()
    {
        var offerBen = false;
        using var http = new HttpClient(new CapturingHandler((request, _) =>
            Voices(request, pro: Volatile.Read(ref offerBen) ? ProVoices : """{"voices":[{"voiceId":"cleo","displayName":"Cleo"}]}""")));
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("magnus", "Magnus"), Voice("ben", "Ben", "lightning_v3.1_pro"));
        host.SetSetting("speech-voice", "lightning_v3.1_pro:ben");
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.DoesNotContain(sut.AvailableVoices, voice => voice.Id == "lightning_v3.1_pro:ben");
        Assert.Equal(SmallestAiPlugin.DefaultVoiceKey, sut.SelectedVoiceId);
        Assert.Equal("lightning_v3.1_pro:ben", host.GetSetting<string>("speech-voice"));

        Volatile.Write(ref offerBen, true);
        await sut.ExecuteSettingsActionAsync("refresh-voices", default);

        Assert.Equal("lightning_v3.1_pro:ben", sut.SelectedVoiceId);
    }

    [Fact]
    public async Task DefaultVoiceIsMagnusRatherThanTheAlphabeticallyFirstVoice()
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => Voices(request)));
        var host = VoiceHost("smallest-key", fetchedAt: null);
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal("lightning_v3.1:aaron", sut.AvailableVoices[0].Id);
        Assert.Equal("lightning_v3.1:magnus", sut.SelectedVoiceId);
    }

    [Fact]
    public async Task DefaultVoiceWithoutMagnusIsAStandardVoiceBeforeAProVoice()
    {
        using var http = new HttpClient(new CapturingHandler((request, _) =>
            Voices(request, standard: """{"voices":[{"voiceId":"zoe","displayName":"Zoe"}]}""")));
        var host = VoiceHost("smallest-key", fetchedAt: null);
        using var sut = new SmallestAiPlugin(http, refreshVoicesAutomatically: true, new TestClock(Now));

        await sut.ActivateAsync(host);
        await sut.VoiceRefreshTask;

        Assert.Equal("lightning_v3.1_pro:ben", sut.AvailableVoices[0].Id);
        Assert.Equal("lightning_v3.1:zoe", sut.SelectedVoiceId);
    }

    [Fact]
    public async Task RefreshVoicesAction_RecordsTheFetchDate()
    {
        using var http = new HttpClient(new CapturingHandler((request, _) => Voices(request)));
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("magnus", "Magnus"));
        using var sut = new SmallestAiPlugin(http, clock: new TestClock(Now));
        await sut.ActivateAsync(host);

        await sut.ExecuteSettingsActionAsync("refresh-voices", default);

        Assert.Equal(Now, sut.VoicesFetchedAt);
        Assert.Equal(Now, host.GetSetting<DateTimeOffset?>(SmallestAiPlugin.VoicesFetchedAtSetting));
        Assert.Equal(RefreshedVoices, sut.AvailableVoices.Select(v => v.Id));
    }

    [Fact]
    public async Task DamagedFetchDateCountsAsNoDate()
    {
        var host = VoiceHost("smallest-key", fetchedAt: null, Voice("magnus", "Magnus"));
        host.SetSetting(SmallestAiPlugin.VoicesFetchedAtSetting, "yesterday");
        using var sut = CreateOfflinePlugin();

        await sut.ActivateAsync(host);

        Assert.Null(sut.VoicesFetchedAt);
    }

    private static bool IsProPool(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("lightning-v3.1-pro", StringComparison.Ordinal);

    private static HttpResponseMessage Voices(HttpRequestMessage request, string standard = StandardVoices, string pro = ProVoices) =>
        JsonResponse(IsProPool(request) ? pro : standard);

    private static SmallestAiPlugin.SpeechVoice Voice(string id, string name, string model = "lightning_v3.1") => new(id, name, model, "en");

    private static TestPluginHostServices VoiceHost(string? key, DateTimeOffset? fetchedAt, params SmallestAiPlugin.SpeechVoice[] voices)
    {
        var host = new TestPluginHostServices();
        if (key is not null) host.Secrets["api-key"] = key;
        if (voices.Length > 0) host.SetSetting("speech-voices", voices);
        if (fetchedAt is { } at) host.SetSetting(SmallestAiPlugin.VoicesFetchedAtSetting, at);
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
            // Ignores cancellation, so the answer can arrive after deactivation or a key change, like a slow server.
            await wait(request);
            return respond(request);
        }
    }
}
