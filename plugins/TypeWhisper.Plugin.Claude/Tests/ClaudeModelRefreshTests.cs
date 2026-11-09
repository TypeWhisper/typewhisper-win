using System.Net;
using System.Text.Json;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Claude;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public partial class ClaudeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string NewCatalog = """{"has_more":false,"data":[{"id":"claude-new"},{"id":"claude-sonnet-5"}]}""";

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Counts requests instead of throwing: a throwing handler would only end up in the refresh's failure log.
    private sealed class RequestLog
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        /// <summary>Returns the number of this request, starting at 1.</summary>
        public int Add() => Interlocked.Increment(ref _count);
    }

    private static ClaudePlugin RefreshingPlugin(TimeProvider clock,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)), refreshModelsAutomatically: true, clock: clock);

    private static void SeedConfiguration(TestPluginHostServices host, string[] models, DateTimeOffset? fetchedAt,
        string key = "saved-key", string? model = null)
    {
        host.Secrets["api-key-saved"] = key;
        host.SetSetting("configuration", new { SecretName = "api-key-saved", Model = model, Models = models, ModelsFetchedAtUtc = fetchedAt });
    }

    private static JsonElement Saved(TestPluginHostServices host) => host.GetSetting<JsonElement>("configuration");
    private static string[] SavedModels(TestPluginHostServices host) =>
        Saved(host).GetProperty("Models").EnumerateArray().Select(model => model.GetString()!).ToArray();
    private static DateTimeOffset? SavedFetchDate(TestPluginHostServices host) =>
        Saved(host).TryGetProperty("ModelsFetchedAtUtc", out var date) && date.ValueKind == JsonValueKind.String ? date.GetDateTimeOffset() : null;

    [Fact]
    public void ThePublicConstructorFollowsTheProcessSwitch()
    {
        // The test project sets the switch, so the package test, which runs the public constructor, stays offline.
        using var published = new ClaudePlugin();
        using var tested = Plugin();
        Assert.Equal(TypeWhisper.Plugin.Shared.ModelCatalogAutoRefresh.AllowedInThisProcess, published.RefreshesModelsAutomatically);
        Assert.False(published.RefreshesModelsAutomatically);
        Assert.False(tested.RefreshesModelsAutomatically);
    }

    [Theory]
    [InlineData(-24)]
    [InlineData(null)]
    [InlineData(1)]
    public async Task ActivationRefreshesAStaleListAndSavesItWithItsDate(int? ageInHours)
    {
        var clock = new TestClock(Now);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(clock, async (request, _) =>
        {
            requests.Add();
            Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", request.RequestUri!.AbsoluteUri);
            Assert.Equal("saved-key", request.Headers.GetValues("x-api-key").Single());
            await release.Task;
            return Json(NewCatalog);
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-retired"], ageInHours is { } hours ? Now.AddHours(hours) : null);

        await plugin.ActivateAsync(host);
        // The saved list stays in use until the new one arrives, also when an older version saved it without a date.
        Assert.Contains(plugin.SupportedModels, m => m.Id == "claude-retired");
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal(["claude-new", "claude-sonnet-5"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
        Assert.Equal(Now, plugin.ModelsFetchedAt);
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "claude-retired");
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ActivationKeepsAListYoungerThanADayWithoutARequest()
    {
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-current"], Now.AddHours(-23));
        var writes = host.SettingWrites;

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["claude-current", "claude-sonnet-5"], plugin.SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ActivationWithoutAKeyStartsNoRefresh()
    {
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        host.SetSetting("configuration", new { Models = new[] { "claude-old" } });

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.False(plugin.IsAvailable);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "claude-old");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshFinishingAfterDeactivationOrDisposalChangesNothing(bool dispose)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var plugin = RefreshingPlugin(new TestClock(Now), async (_, ct) =>
        {
            started.SetResult(ct);
            // Ignores cancellation, so the answer arrives late, like a slow server.
            await release.Task;
            return Json(NewCatalog);
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-old"], null);
        await plugin.ActivateAsync(host);
        var requestToken = await started.Task;
        var writes = host.SettingWrites;

        if (dispose) plugin.Dispose(); else await plugin.DeactivateAsync();
        Assert.True(requestToken.IsCancellationRequested);
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["claude-old"], SavedModels(host));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
        Assert.Empty(host.Logs);
        if (!dispose) plugin.Dispose();
    }

    [Fact]
    public async Task KeyChangeDiscardsTheOldAccountsListAndRefreshesWithTheNewKey()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keys = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var plugin = RefreshingPlugin(new TestClock(Now), async (request, _) =>
        {
            keys.Enqueue(request.Headers.GetValues("x-api-key").Single());
            await release.Task;
            return Json("""{"has_more":false,"data":[{"id":"claude-second-account"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-first-account"], Now, model: "claude-opus-5");
        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Empty(keys);

        await plugin.SetApiKeyAsync("second-key");
        // The first account's list is gone before the second one arrives.
        Assert.Empty(SavedModels(host));
        Assert.Null(SavedFetchDate(host));
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "claude-first-account");
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(["second-key"], keys);
        Assert.Equal(["claude-second-account"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
        // The selected model is not tied to the account.
        Assert.Equal("claude-opus-5", Field(plugin, "selectedLlmModel"));
        Assert.Equal("claude-opus-5", Saved(host).GetProperty("Model").GetString());

        // Removing the key discards the list too and starts nothing.
        await plugin.SetApiKeyAsync("");
        await plugin.ModelRefreshTask;
        Assert.Equal(["second-key"], keys);
        Assert.Empty(SavedModels(host));
    }

    [Fact]
    public async Task RefreshForAnOldKeyCannotSaveItsListAfterAKeyChange()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (request, _) =>
        {
            if (request.Headers.GetValues("x-api-key").Single() == "second-key")
                return Json("""{"has_more":false,"data":[{"id":"claude-second-account"}]}""");
            firstStarted.SetResult();
            await releaseFirst.Task;
            return Json("""{"has_more":false,"data":[{"id":"claude-first-account"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-old"], null, key: "first-key");
        await plugin.ActivateAsync(host);
        await firstStarted.Task;
        var first = plugin.ModelRefreshTask;

        await plugin.SetApiKeyAsync("second-key");
        await plugin.ModelRefreshTask;
        releaseFirst.SetResult();
        await first;

        Assert.Equal(["claude-second-account"], SavedModels(host));
        Assert.Contains(plugin.SupportedModels, m => m.Id == "claude-second-account");
        Assert.DoesNotContain(plugin.SupportedModels, m => m.Id == "claude-first-account");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":{"message":"fixture-provider-detail"}}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"message":"fixture-provider-detail"}}""")]
    [InlineData(HttpStatusCode.OK, """{"has_more":false,"data":[]}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task FailedOrEmptyRefreshKeepsTheSavedListAndLogsNoSecrets(HttpStatusCode status, string body)
    {
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => Task.FromResult(Json(body, status)));
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-working"], Now.AddDays(-2), key: "secret-fixture-key");
        var writes = host.SettingWrites;

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["claude-working"], SavedModels(host));
        Assert.Equal(Now.AddDays(-2), SavedFetchDate(host));
        Assert.Contains(plugin.SupportedModels, m => m.Id == "claude-working");
        var warning = Assert.Single(host.Logs, log => log.Level == PluginLogLevel.Warning).Message;
        Assert.DoesNotContain("secret-fixture-key", warning);
        Assert.DoesNotContain("fixture-provider-detail", warning);
        Assert.DoesNotContain("not json", warning);
    }

    [Fact]
    public async Task SavedModelSurvivesAListThatLacksIt()
    {
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) =>
            Task.FromResult(Json("""{"has_more":false,"data":[{"id":"claude-new"}]}""")));
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-sonnet-4-6", "claude-new"], null, model: "claude-sonnet-4-6");

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(["claude-new"], SavedModels(host));
        Assert.Equal("claude-sonnet-4-6", Saved(host).GetProperty("Model").GetString());
        Assert.Equal("claude-sonnet-4-6", Field(plugin, "selectedLlmModel"));
        Assert.True(plugin.SupportedModels.Single(m => m.Id == "claude-sonnet-4-6").IsRecommended);
    }

    [Fact]
    public async Task RefreshButtonSavesTheFetchDateWithTheList()
    {
        var clock = new TestClock(Now);
        using var plugin = new ClaudePlugin(new HttpClient(new Handler((_, _) => Task.FromResult(Json(NewCatalog)))), clock: clock);
        var host = new TestPluginHostServices();
        await Configure(plugin, host);
        Assert.Null(SavedFetchDate(host));

        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        clock.Now = Now.AddMinutes(5);
        await plugin.SaveProfileSettingsAsync("claude", Empty, null, default);

        Assert.Equal(["claude-new", "claude-sonnet-5"], SavedModels(host));
        // The date says when the list was fetched, not when it was saved.
        Assert.Equal(Now, SavedFetchDate(host));
        Assert.Equal(Now, plugin.ModelsFetchedAt);
    }

    [Fact]
    public async Task RefreshButtonReplacesARunningBackgroundRefreshAndTheDraftWinsOnSave()
    {
        var clock = new TestClock(Now);
        var automaticStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutomatic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var plugin = RefreshingPlugin(clock, async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                automaticStarted.SetResult();
                await releaseAutomatic.Task;
                return Json("""{"has_more":false,"data":[{"id":"claude-automatic"}]}""");
            }
            return Json("""{"has_more":false,"data":[{"id":"claude-draft"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-old"], null);
        await plugin.ActivateAsync(host);
        await automaticStarted.Task;

        Assert.True((await plugin.ExecuteProfileActionAsync("claude", "refreshModels", Empty, null, default)).HasPendingChanges);
        clock.Now = Now.AddMinutes(1);
        releaseAutomatic.SetResult();
        await plugin.ModelRefreshTask;

        // The background refresh was replaced, so its late answer saves nothing; the editor offers the draft.
        Assert.Equal(["claude-old"], SavedModels(host));
        Assert.Null(SavedFetchDate(host));
        var choices = plugin.TextSettings.Single(f => f.Id == "selectedLlmModel").Choices.Select(c => c.Value).ToArray();
        Assert.Contains("claude-draft", choices);
        Assert.DoesNotContain("claude-automatic", choices);

        // Saving keeps the list the user fetched, with the time it was fetched.
        await plugin.SaveProfileSettingsAsync("claude", Empty, null, default);
        Assert.Equal(["claude-draft"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
    }

    [Fact]
    public async Task SaveWithoutADraftKeepsAnAutomaticListThatArrivedDuringTheSave()
    {
        var automaticStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAutomatic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (_, _) =>
        {
            automaticStarted.SetResult();
            await releaseAutomatic.Task;
            return Json("""{"has_more":false,"data":[{"id":"claude-automatic"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-old"], null);
        await plugin.ActivateAsync(host);
        await automaticStarted.Task;
        var secretStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecret = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeStoreSecret = async () => { secretStarted.SetResult(); await releaseSecret.Task; };

        // Entering the saved key again is no key change. The edit was made while the old list was saved.
        var save = plugin.SaveProfileSettingsAsync("claude", new Dictionary<string, string> { ["llmTemperatureMode"] = "custom" },
            "saved-key", default);
        await secretStarted.Task;
        releaseAutomatic.SetResult();
        await plugin.ModelRefreshTask;
        Assert.Equal(["claude-automatic"], SavedModels(host));
        releaseSecret.SetResult();
        await save;

        Assert.Equal(["claude-automatic"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
        Assert.Equal("custom", Saved(host).GetProperty("TemperatureMode").GetString());
        Assert.Equal("saved-key", host.Secrets[Saved(host).GetProperty("SecretName").GetString()!]);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("42")]
    [InlineData("{\"at\":\"2026-11-09T12:00:00Z\"}")]
    [InlineData("[\"2026-11-09T12:00:00Z\"]")]
    [InlineData("\"0001-01-01T00:00:00+00:00\"")]
    public async Task DamagedFetchDateReadsAsNoDate(string damaged)
    {
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        host.Secrets["api-key-saved"] = "saved-key";
        using (var configuration = JsonDocument.Parse(
            $$"""{"SecretName":"api-key-saved","TemperatureMode":"custom","Models":["claude-old"],"ModelsFetchedAtUtc":{{damaged}}}"""))
            host.SetSetting("configuration", configuration.RootElement);

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal("custom", Field(plugin, "llmTemperatureMode"));
        Assert.Equal(["claude-new", "claude-sonnet-5"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
    }

    [Fact]
    public async Task SavedFetchDateSurvivesARestart()
    {
        var host = new TestPluginHostServices();
        using (var first = RefreshingPlugin(new TestClock(Now), (_, _) => Task.FromResult(Json(NewCatalog))))
        {
            SeedConfiguration(host, ["claude-old"], null);
            await first.ActivateAsync(host);
            await first.ModelRefreshTask;
        }
        var requests = new RequestLog();
        using var restarted = RefreshingPlugin(new TestClock(Now.AddHours(23)), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });

        await restarted.ActivateAsync(host);
        await restarted.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(Now, restarted.ModelsFetchedAt);
        Assert.Equal(["claude-new", "claude-sonnet-5"], restarted.SupportedModels.Select(m => m.Id));
    }

    // A recheck that starts after Refresh models must not replace the list the user then saves.
    [Fact]
    public async Task ARecheckStartedBeforeSavingADraftCannotReplaceIt()
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        var releaseRecheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(clock, async (_, _) =>
        {
            if (requests.Add() == 1)
                return Json("""{"has_more":false,"data":[{"id":"claude-draft"}]}""");
            await releaseRecheck.Task;
            return Json("""{"has_more":false,"data":[{"id":"claude-automatic"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-current"], Now);
        await plugin.ActivateAsync(host);

        Assert.True((await plugin.ExecuteProfileActionAsync("claude", "refreshModels", Empty, null, default)).HasPendingChanges);
        clock.Advance(TimeSpan.FromHours(24));
        var recheck = plugin.ModelRefreshTask;
        await plugin.SaveProfileSettingsAsync("claude", Empty, null, default);
        releaseRecheck.SetResult();
        await recheck.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, requests.Count);
        Assert.Equal(["claude-draft"], SavedModels(host));
    }

    [Fact]
    public async Task HourlyRecheckReloadsAListThatAgesWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(clock, (request, _) =>
        {
            requests.Add();
            Assert.Equal("saved-key", request.Headers.GetValues("x-api-key").Single());
            return Task.FromResult(Json(NewCatalog));
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-current"], Now);

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Equal(0, requests.Count);
        Assert.Equal(1, clock.ActiveTimers);

        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal(["claude-new", "claude-sonnet-5"], SavedModels(host));
        Assert.Equal(Now.AddHours(24), SavedFetchDate(host));
        Assert.Equal(Now.AddHours(24), plugin.ModelsFetchedAt);
        Assert.Contains(plugin.SupportedModels, m => m.Id == "claude-new");

        // The new date counts from now, so the next check leaves the list alone.
        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await plugin.ModelRefreshTask;
        Assert.Equal(1, requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeactivationOrDisposalStopsTheHourlyRecheck(bool dispose)
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        var plugin = RefreshingPlugin(clock, (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-current"], Now);
        await plugin.ActivateAsync(host);
        Assert.Equal(1, clock.ActiveTimers);

        if (dispose) plugin.Dispose(); else await plugin.DeactivateAsync();
        // A live timer would keep the plugin and its assembly loaded.
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(["claude-current"], SavedModels(host));
        if (!dispose) plugin.Dispose();
    }

    [Fact]
    public async Task HourlyRecheckRetriesAFailedRefresh()
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        // The first attempt fails like a PC that is still offline after startup.
        using var plugin = RefreshingPlugin(clock, (_, _) => requests.Add() == 1
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))
            : Task.FromResult(Json(NewCatalog)));
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-working"], Now.AddDays(-2));

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Equal(1, requests.Count);
        Assert.Equal(["claude-working"], SavedModels(host));
        Assert.Single(host.Logs, log => log.Level == PluginLogLevel.Warning);

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await plugin.ModelRefreshTask;

        Assert.Equal(2, requests.Count);
        Assert.Equal(["claude-new", "claude-sonnet-5"], SavedModels(host));
        Assert.Equal(Now + ModelCatalogAutoRefresh.RecheckInterval, SavedFetchDate(host));
        Assert.Single(host.Logs);
    }

    [Fact]
    public async Task HourlyRecheckStartsNothingAfterTheKeyIsRemoved()
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(clock, (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["claude-current"], Now);
        await plugin.ActivateAsync(host);

        // Removing the key also discards the list, so only the missing key keeps the check from refreshing.
        await plugin.SetApiKeyAsync("");
        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Empty(SavedModels(host));
    }
}
