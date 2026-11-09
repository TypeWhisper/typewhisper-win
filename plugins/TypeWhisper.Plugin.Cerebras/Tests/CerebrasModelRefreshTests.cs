using System.Net;
using System.Text.Json;
using PortableMigration.Tests;
using TypeWhisper.Plugin.Cerebras;
using TypeWhisper.Plugin.Shared;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public partial class CerebrasTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string NewCatalog = """{"data":[{"id":"zai-glm-4.7"},{"id":"gpt-oss-120b"}]}""";

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

    private static CerebrasPlugin RefreshingPlugin(TimeProvider clock,
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
    // The settings page sends every field it rendered; a page opened before the refresh must not replace the model.
    [Fact]
    public async Task AnEditorOpenedBeforeTheRefreshKeepsTheSavedModelOnSave()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (_, _) =>
        {
            await release.Task;
            return Json("""{"data":[{"id":"gpt-oss-120b"},{"id":"llama3.1-8b"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["gpt-oss-120b", "llama3.1-8b"], Now, model: "llama3.1-8b");
        await plugin.ActivateAsync(host);
        await plugin.SetApiKeyAsync("second-key");
        var editor = RenderEditor(plugin);
        release.SetResult();
        await plugin.ModelRefreshTask;

        editor["llmTemperatureMode"] = "custom";
        await plugin.SaveProfileSettingsAsync("cerebras", editor, null, default);

        Assert.Equal("llama3.1-8b", SavedModel(host));
        Assert.Equal("custom", Saved(host).GetProperty("TemperatureMode").GetString());
    }

    [Fact]
    public async Task AnEditorOpenedBeforeARefreshThatDropsTheSavedModelStillSaves()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (_, _) =>
        {
            await release.Task;
            return Json("""{"data":[{"id":"gpt-oss-120b"},{"id":"zai-glm-4.7"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["gpt-oss-120b", "llama3.1-8b"], null, model: "llama3.1-8b");
        await plugin.ActivateAsync(host);
        var editor = RenderEditor(plugin);
        release.SetResult();
        await plugin.ModelRefreshTask;

        editor["llmTemperatureMode"] = "custom";
        await plugin.SaveProfileSettingsAsync("cerebras", editor, null, default);

        Assert.Equal("llama3.1-8b", SavedModel(host));
        Assert.Equal("gpt-oss-120b", UsedModel(plugin));
    }

    // Without a saved model the field shows the list's default. A refresh can drop that model while the page is open.
    [Fact]
    public async Task AnEditorOpenedBeforeARefreshThatDropsTheShownDefaultStillSaves()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (_, _) =>
        {
            await release.Task;
            return Json("""{"data":[{"id":"qwen-3.8-27b"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, [], null);
        await plugin.ActivateAsync(host);
        var editor = RenderEditor(plugin);
        Assert.Equal("gpt-oss-120b", editor["selectedLlmModel"]);
        release.SetResult();
        await plugin.ModelRefreshTask;

        editor["llmTemperatureMode"] = "custom";
        await plugin.SaveProfileSettingsAsync("cerebras", editor, null, default);

        Assert.Null(SavedModel(host));
        Assert.Equal("custom", Saved(host).GetProperty("TemperatureMode").GetString());
        Assert.Equal("qwen-3.8-27b", UsedModel(plugin));
    }

    // A model the page never showed and the list does not offer is still refused.
    [Fact]
    public async Task AModelThatWasNeverShownAndIsNotListedIsRefused()
    {
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => Task.FromResult(Json("""{"data":[{"id":"qwen-3.8-27b"}]}""")));
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["qwen-3.8-27b"], Now);
        await plugin.ActivateAsync(host);
        var editor = RenderEditor(plugin);

        editor["selectedLlmModel"] = "made-up-model";

        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveProfileSettingsAsync("cerebras", editor, null, default));
        Assert.Null(SavedModel(host));
    }

    private static Dictionary<string, string> RenderEditor(CerebrasPlugin plugin) =>
        plugin.TextSettings.Where(f => f.Id != plugin.ProfileSelectorId).ToDictionary(f => f.Id, f => f.Value);

    // The model requests use when a workflow names none; the field may show a saved model the list lacks.
    private static string UsedModel(CerebrasPlugin plugin) => plugin.SupportedModels.Single(m => m.IsRecommended).Id;

    private static PluginSettingChoice Choice(CerebrasPlugin plugin, string id) =>
        plugin.TextSettings.Single(f => f.Id == "selectedLlmModel").Choices.Single(c => c.Value == id);

    private static DateTimeOffset? SavedFetchDate(TestPluginHostServices host) =>
        Saved(host).TryGetProperty("ModelsFetchedAtUtc", out var date) && date.ValueKind == JsonValueKind.String ? date.GetDateTimeOffset() : null;
    private static string? SavedModel(TestPluginHostServices host) =>
        Saved(host).TryGetProperty("Model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
    private static string BearerKey(HttpRequestMessage request) => request.Headers.Authorization!.Parameter!;

    [Fact]
    public void ThePublicConstructorFollowsTheProcessSwitch()
    {
        // The test project sets the switch, so the package test, which runs the public constructor, stays offline.
        using var published = new CerebrasPlugin();
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
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), async (request, _) =>
        {
            requests.Add();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.cerebras.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("saved-key", BearerKey(request));
            await release.Task;
            return Json(NewCatalog);
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], ageInHours is { } hours ? Now.AddHours(hours) : null);

        await plugin.ActivateAsync(host);
        // The saved list stays in use until the new one arrives, also when an older version saved it without a date.
        Assert.Equal(["llama3.1-8b"], plugin.SupportedModels.Select(m => m.Id));
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
        Assert.Equal(Now, plugin.ModelsFetchedAt);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task ActivationKeepsAListYoungerThanADayWithoutARequest()
    {
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["qwen-3.8-27b"], Now.AddHours(-23));
        var writes = host.SettingWrites;

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["qwen-3.8-27b"], plugin.SupportedModels.Select(m => m.Id));
    }

    [Fact]
    public async Task ActivationWithoutAKeyStartsNoRefresh()
    {
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });
        var host = new TestPluginHostServices();
        host.SetSetting("configuration", new { Models = new[] { "llama3.1-8b" } });

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.False(plugin.IsAvailable);
        Assert.Equal(["llama3.1-8b"], plugin.SupportedModels.Select(m => m.Id));
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
        SeedConfiguration(host, ["llama3.1-8b"], null);
        await plugin.ActivateAsync(host);
        var requestToken = await started.Task;
        var writes = host.SettingWrites;

        if (dispose) plugin.Dispose(); else await plugin.DeactivateAsync();
        Assert.True(requestToken.IsCancellationRequested);
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["llama3.1-8b"], SavedModels(host));
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
            keys.Enqueue(BearerKey(request));
            await release.Task;
            return Json("""{"data":[{"id":"second-account-model"},{"id":"qwen-3.8-27b"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["first-account-model", "qwen-3.8-27b"], Now, model: "qwen-3.8-27b");
        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Empty(keys);

        await plugin.SetApiKeyAsync("second-key");
        // The first account's list is gone before the second one arrives.
        Assert.Empty(SavedModels(host));
        Assert.Null(SavedFetchDate(host));
        Assert.Equal(["gpt-oss-120b", "qwen-3.8-27b"], plugin.SupportedModels.Select(m => m.Id));
        release.SetResult();
        await plugin.ModelRefreshTask;

        Assert.Equal(["second-key"], keys);
        Assert.Equal(["qwen-3.8-27b", "second-account-model"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
        // The selected model is not tied to the account.
        Assert.Equal("qwen-3.8-27b", Field(plugin, "selectedLlmModel"));
        Assert.Equal("qwen-3.8-27b", SavedModel(host));

        // Removing the key discards the list too and starts nothing.
        await plugin.SetApiKeyAsync("");
        await plugin.ModelRefreshTask;
        Assert.Equal(["second-key"], keys);
        Assert.Empty(SavedModels(host));
        Assert.Equal("qwen-3.8-27b", SavedModel(host));
    }

    [Fact]
    public async Task RefreshForAnOldKeyCannotSaveItsListAfterAKeyChange()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = RefreshingPlugin(new TestClock(Now), async (request, _) =>
        {
            if (BearerKey(request) == "second-key")
                return Json("""{"data":[{"id":"second-account-model"}]}""");
            firstStarted.SetResult();
            await releaseFirst.Task;
            return Json("""{"data":[{"id":"first-account-model"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], null, key: "first-key");
        await plugin.ActivateAsync(host);
        await firstStarted.Task;
        var first = plugin.ModelRefreshTask;

        await plugin.SetApiKeyAsync("second-key");
        await plugin.ModelRefreshTask;
        releaseFirst.SetResult();
        await first;

        Assert.Equal(["second-account-model"], SavedModels(host));
        Assert.Equal(["second-account-model"], plugin.SupportedModels.Select(m => m.Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"message":"fixture-provider-detail"}""")]
    [InlineData(HttpStatusCode.PaymentRequired, """{"message":"fixture-provider-detail"}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[]}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task FailedOrEmptyRefreshKeepsTheSavedListAndLogsNoSecrets(HttpStatusCode status, string body)
    {
        using var plugin = RefreshingPlugin(new TestClock(Now), (_, _) => Task.FromResult(Json(body, status)));
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b", "qwen-3.8-27b"], Now.AddDays(-2), key: "secret-fixture-key");
        var writes = host.SettingWrites;

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(["llama3.1-8b", "qwen-3.8-27b"], SavedModels(host));
        Assert.Equal(Now.AddDays(-2), SavedFetchDate(host));
        Assert.Equal(["llama3.1-8b", "qwen-3.8-27b"], plugin.SupportedModels.Select(m => m.Id));
        var warning = Assert.Single(host.Logs, log => log.Level == PluginLogLevel.Warning).Message;
        Assert.DoesNotContain("secret-fixture-key", warning);
        Assert.DoesNotContain("fixture-provider-detail", warning);
        Assert.DoesNotContain("not json", warning);
    }

    [Fact]
    public async Task SavedModelSurvivesAListThatLacksItAndAppliesAgainOnceListed()
    {
        var catalog = """{"data":[{"id":"a-model"},{"id":"gpt-oss-120b"},{"id":"zai-glm-4.7"}]}""";
        string? chatModel = null;
        Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct) => request.Method == HttpMethod.Get
            ? Task.FromResult(Json(catalog))
            : ChatAsync(request, ct);
        async Task<HttpResponseMessage> ChatAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            chatModel = body.RootElement.GetProperty("model").GetString();
            return Json(Answer);
        }
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b", "qwen-3.8-27b"], null, model: "llama3.1-8b");
        using (var plugin = RefreshingPlugin(new TestClock(Now), Send))
        {
            await plugin.ActivateAsync(host);
            await plugin.ModelRefreshTask;

            // GPT-OSS 120B is the default while the list lacks the saved model, not the alphabetically first entry.
            Assert.True(plugin.SupportedModels.Single(m => m.Id == "gpt-oss-120b").IsRecommended);
            await plugin.ProcessAsync("", "hello", "", default);
            Assert.Equal("gpt-oss-120b", chatModel);
            // The field keeps showing the saved model and names the model that requests use meanwhile.
            Assert.Equal("llama3.1-8b", Field(plugin, "selectedLlmModel"));
            Assert.Contains("GPT-OSS 120B", Choice(plugin, "llama3.1-8b").Title);
            // Saving other settings, with the shown value sent back, keeps the saved model.
            await plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string>
            { ["selectedLlmModel"] = Field(plugin, "selectedLlmModel"), ["llmTemperatureMode"] = "custom" }, null, default);
            Assert.Equal("llama3.1-8b", SavedModel(host));
            Assert.Equal("custom", Saved(host).GetProperty("TemperatureMode").GetString());
        }

        catalog = """{"data":[{"id":"gpt-oss-120b"},{"id":"llama3.1-8b"}]}""";
        using var restarted = RefreshingPlugin(new TestClock(Now.AddDays(1)), Send);
        await restarted.ActivateAsync(host);
        await restarted.ModelRefreshTask;

        Assert.Equal("llama3.1-8b", Field(restarted, "selectedLlmModel"));
        await restarted.ProcessAsync("", "hello", "", default);
        Assert.Equal("llama3.1-8b", chatModel);
    }

    [Fact]
    public async Task DefaultFallsBackToQwenAndThenToTheFirstListedModel()
    {
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["a-model", "qwen-3.8-27b"], Now, model: "retired-model");
        using (var plugin = Plugin())
        {
            await plugin.ActivateAsync(host);
            Assert.Equal("qwen-3.8-27b", UsedModel(plugin));
        }
        SeedConfiguration(host, ["b-model", "a-model"], Now, model: "retired-model");
        using var other = Plugin();
        await other.ActivateAsync(host);
        Assert.Equal("a-model", UsedModel(other));
        Assert.Equal("retired-model", SavedModel(host));
    }

    [Fact]
    public async Task RefreshButtonSavesTheFetchDateAndKeepsTheSavedModel()
    {
        var clock = new TestClock(Now);
        using var plugin = new CerebrasPlugin(new HttpClient(new Handler((_, _) =>
            Task.FromResult(Json("""{"data":[{"id":"z-model"},{"id":"a-model"}]}""")))), clock: clock);
        var host = new TestPluginHostServices();
        await Configure(plugin, host);
        await plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string> { ["selectedLlmModel"] = "qwen-3.8-27b" }, null, default);
        Assert.Null(SavedFetchDate(host));

        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        clock.Now = Now.AddMinutes(5);
        await plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string> { ["selectedLlmModel"] = "qwen-3.8-27b" }, null, default);

        Assert.Equal(["a-model", "z-model"], SavedModels(host));
        // The date says when the list was fetched, not when it was saved.
        Assert.Equal(Now, SavedFetchDate(host));
        Assert.Equal(Now, plugin.ModelsFetchedAt);
        // The new list lacks the saved model: it stays saved and shown, and the first listed model is used meanwhile.
        Assert.Equal("qwen-3.8-27b", SavedModel(host));
        Assert.Equal("qwen-3.8-27b", Field(plugin, "selectedLlmModel"));
        Assert.Equal("a-model", UsedModel(plugin));
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
                return Json("""{"data":[{"id":"automatic-model"}]}""");
            }
            return Json("""{"data":[{"id":"draft-model"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], null);
        await plugin.ActivateAsync(host);
        await automaticStarted.Task;

        Assert.True((await plugin.ExecuteProfileActionAsync("cerebras", "refreshModels", Empty, null, default)).HasPendingChanges);
        clock.Now = Now.AddMinutes(1);
        releaseAutomatic.SetResult();
        await plugin.ModelRefreshTask;

        // The background refresh was replaced, so its late answer saves nothing; the editor offers the draft.
        Assert.Equal(["llama3.1-8b"], SavedModels(host));
        Assert.Null(SavedFetchDate(host));
        var choices = plugin.TextSettings.Single(f => f.Id == "selectedLlmModel").Choices.Select(c => c.Value).ToArray();
        Assert.Contains("draft-model", choices);
        Assert.DoesNotContain("automatic-model", choices);

        // Saving keeps the list the user fetched, with the time it was fetched.
        await plugin.SaveProfileSettingsAsync("cerebras", Empty, null, default);
        Assert.Equal(["draft-model"], SavedModels(host));
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
            return Json("""{"data":[{"id":"automatic-model"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], null);
        await plugin.ActivateAsync(host);
        await automaticStarted.Task;
        var secretStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecret = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeStoreSecret = async () => { secretStarted.SetResult(); await releaseSecret.Task; };

        // Entering the saved key again is no key change. The edit was made while the old list was saved.
        var save = plugin.SaveProfileSettingsAsync("cerebras", new Dictionary<string, string> { ["llmTemperatureMode"] = "custom" },
            "saved-key", default);
        await secretStarted.Task;
        releaseAutomatic.SetResult();
        await plugin.ModelRefreshTask;
        Assert.Equal(["automatic-model"], SavedModels(host));
        releaseSecret.SetResult();
        await save;

        Assert.Equal(["automatic-model"], SavedModels(host));
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
            $$"""{"SecretName":"api-key-saved","TemperatureMode":"custom","Models":["llama3.1-8b"],"ModelsFetchedAtUtc":{{damaged}}}"""))
            host.SetSetting("configuration", configuration.RootElement);

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal("custom", Field(plugin, "llmTemperatureMode"));
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], SavedModels(host));
        Assert.Equal(Now, SavedFetchDate(host));
    }

    [Fact]
    public async Task SavedFetchDateSurvivesARestart()
    {
        var host = new TestPluginHostServices();
        using (var first = RefreshingPlugin(new TestClock(Now), (_, _) => Task.FromResult(Json(NewCatalog))))
        {
            SeedConfiguration(host, ["llama3.1-8b"], null);
            await first.ActivateAsync(host);
            await first.ModelRefreshTask;
        }
        var requests = new RequestLog();
        using var restarted = RefreshingPlugin(new TestClock(Now.AddHours(23)), (_, _) => { requests.Add(); return Task.FromResult(Json(NewCatalog)); });

        await restarted.ActivateAsync(host);
        await restarted.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(Now, restarted.ModelsFetchedAt);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], restarted.SupportedModels.Select(m => m.Id));
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
                return Json("""{"data":[{"id":"draft-model"}]}""");
            await releaseRecheck.Task;
            return Json("""{"data":[{"id":"automatic-model"}]}""");
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], Now);
        await plugin.ActivateAsync(host);

        Assert.True((await plugin.ExecuteProfileActionAsync("cerebras", "refreshModels", Empty, null, default)).HasPendingChanges);
        clock.Advance(TimeSpan.FromHours(24));
        var recheck = plugin.ModelRefreshTask;
        await plugin.SaveProfileSettingsAsync("cerebras", Empty, null, default);
        releaseRecheck.SetResult();
        await recheck.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, requests.Count);
        Assert.Equal(["draft-model"], SavedModels(host));
    }

    [Fact]
    public async Task HourlyRecheckReloadsAListThatAgesWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(Now);
        var requests = new RequestLog();
        using var plugin = RefreshingPlugin(clock, (request, _) =>
        {
            requests.Add();
            Assert.Equal("saved-key", BearerKey(request));
            return Task.FromResult(Json(NewCatalog));
        });
        var host = new TestPluginHostServices();
        SeedConfiguration(host, ["llama3.1-8b"], Now);

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Equal(0, requests.Count);
        Assert.Equal(1, clock.ActiveTimers);

        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(1, requests.Count);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], SavedModels(host));
        Assert.Equal(Now.AddHours(24), SavedFetchDate(host));
        Assert.Equal(Now.AddHours(24), plugin.ModelsFetchedAt);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], plugin.SupportedModels.Select(m => m.Id));

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
        SeedConfiguration(host, ["llama3.1-8b"], Now);
        await plugin.ActivateAsync(host);
        Assert.Equal(1, clock.ActiveTimers);

        if (dispose) plugin.Dispose(); else await plugin.DeactivateAsync();
        // A live timer would keep the plugin and its assembly loaded.
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Equal(["llama3.1-8b"], SavedModels(host));
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
        SeedConfiguration(host, ["llama3.1-8b", "qwen-3.8-27b"], Now.AddDays(-2));

        await plugin.ActivateAsync(host);
        await plugin.ModelRefreshTask;
        Assert.Equal(1, requests.Count);
        Assert.Equal(["llama3.1-8b", "qwen-3.8-27b"], SavedModels(host));
        Assert.Single(host.Logs, log => log.Level == PluginLogLevel.Warning);

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await plugin.ModelRefreshTask;

        Assert.Equal(2, requests.Count);
        Assert.Equal(["gpt-oss-120b", "zai-glm-4.7"], SavedModels(host));
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
        SeedConfiguration(host, ["llama3.1-8b"], Now);
        await plugin.ActivateAsync(host);

        // Removing the key also discards the list, so only the missing key keeps the check from refreshing.
        await plugin.SetApiKeyAsync("");
        clock.Advance(TimeSpan.FromHours(24));
        await plugin.ModelRefreshTask;

        Assert.Equal(0, requests.Count);
        Assert.Empty(SavedModels(host));
    }
}
