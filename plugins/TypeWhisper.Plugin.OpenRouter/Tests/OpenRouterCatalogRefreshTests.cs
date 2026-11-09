using System.Net;
using System.Text;
using System.Text.Json;
using PortableMigration.Tests;
using TypeWhisper.Plugin.OpenRouter;
using TypeWhisper.Plugin.Shared;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenRouterPluginTests
{
    private static readonly DateTimeOffset RefreshNow = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private const string RefreshedText = """{"data":[{"id":"provider/new-text","name":"New text","architecture":{"modality":"text->text"}}]}""";
    private const string RefreshedSpeech = """{"data":[{"id":"provider/new-speech","name":"New speech"}]}""";

    [Theory]
    [InlineData(null)]
    [InlineData(24)]
    public async Task Activation_RefreshesUndatedOrDayOldListsAndSavesTheirDates(int? hoursOld)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            Assert.Equal("Bearer openrouter-key", request.Headers.Authorization?.ToString());
            await release.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var fetchedAt = hoursOld is { } hours ? RefreshNow.AddHours(-hours) : (DateTimeOffset?)null;
        var host = await ConfiguredHostAsync(fetchedAt, fetchedAt);
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        // The saved lists stay in use until the refresh delivers new ones.
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        release.SetResult();
        await sut.CatalogRefreshTask;

        Assert.Equal(["https://openrouter.ai/api/v1/models", "https://openrouter.ai/api/v1/models?output_modalities=transcription"], handler.Requests);
        Assert.Equal([OpenRouterPlugin.DefaultLlmModelId, "provider/new-text"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["provider/new-speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(RefreshNow, sut.TextModelsFetchedAt);
        Assert.Equal(RefreshNow, sut.SpeechModelsFetchedAt);
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(RefreshNow, SavedDate(host, "SpeechModelsFetchedAt"));
        Assert.Equal(1, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task Activation_RefreshesListsSavedBeforeTheConfigurationRecordWithoutDiscardingThemFirst()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "openrouter-key";
        host.SetSetting("fetchedModels", new List<OpenRouterFetchedModel> { new("legacy/text", "Legacy text", "0", "0") });
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        Assert.Contains(sut.SupportedModels, model => model.Id == "legacy/text");
        release.SetResult();
        await sut.CatalogRefreshTask;

        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "legacy/text");
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal("api-key", SavedConfiguration(host).GetProperty("SecretName").GetString());
    }

    [Fact]
    public async Task Activation_RefreshesOnlyTheListThatIsDue()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech)));
        var host = await ConfiguredHostAsync(RefreshNow.AddHours(-23), RefreshNow.AddDays(-3));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(["https://openrouter.ai/api/v1/models?output_modalities=transcription"], handler.Requests);
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        Assert.Equal(["provider/new-speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(RefreshNow.AddHours(-23), SavedDate(host, "TextModelsFetchedAt"));
    }

    [Fact]
    public async Task Activation_KeepsListsYoungerThanADayWithoutRequests()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        var host = await ConfiguredHostAsync(RefreshNow.AddHours(-23), RefreshNow.AddHours(-1));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
    }

    [Fact]
    public async Task Activation_WithoutApiKeyDoesNotRefresh()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(new TestPluginHostServices());
        await sut.CatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.Equal(OpenRouterPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
    }

    [Fact]
    public async Task DamagedFetchDatesReadAsNoDateAndRefresh()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech)));
        var host = new TestPluginHostServices();
        host.SetSetting("configuration", new
        {
            SpeechModel = (string?)null, TextModel = (string?)null, UserSelectedTextModel = false,
            TemperatureMode = "providerDefault", Temperature = 0.3,
            SpeechModels = new[] { new OpenRouterFetchedModel("saved/speech", "Saved speech", "0", "0") },
            TextModels = new[] { new OpenRouterFetchedModel("saved/text", "Saved text", "0", "0") },
            SecretName = "saved-key",
            SpeechModelsFetchedAt = 42,
            TextModelsFetchedAt = "not a date",
        });
        await host.StoreSecretAsync("saved-key", "openrouter-key");
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(RefreshNow, SavedDate(host, "SpeechModelsFetchedAt"));
    }

    [Fact]
    public async Task RefreshFinishingAfterDeactivationChangesNothing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, like a slow server whose answer arrives after deactivation.
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = await ConfiguredHostAsync(null, null);
        var writes = host.SettingWrites;
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await handler.Started;
        await sut.DeactivateAsync();
        release.SetResult();
        await sut.CatalogRefreshTask;

        Assert.Equal(writes, host.SettingWrites);
        Assert.Null(SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task KeyChange_DropsARefreshStartedWithTheOldKeyAndRefreshesWithTheNewOne()
    {
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            if (request.Headers.Authorization?.Parameter == "openrouter-key")
            {
                await releaseFirst.Task;
                return CatalogResponse(request,
                    """{"data":[{"id":"provider/first-text","name":"First text","architecture":{"modality":"text->text"}}]}""",
                    """{"data":[{"id":"provider/first-speech","name":"First speech"}]}""");
            }

            Assert.Equal("Bearer second-key", request.Headers.Authorization?.ToString());
            await releaseSecond.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = await ConfiguredHostAsync(null, null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await handler.Started;
        var firstRefresh = sut.CatalogRefreshTask;

        await sut.SetApiKeyAsync("second-key");

        // OpenRouter lists are the same for every key, so the saved one stays until the new one arrives.
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        releaseSecond.SetResult();
        await sut.CatalogRefreshTask;
        releaseFirst.SetResult();
        await firstRefresh;

        Assert.Equal([OpenRouterPlugin.DefaultLlmModelId, "provider/new-text"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["provider/new-speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
    }

    [Fact]
    public async Task KeyChange_KeepsAFreshListWithoutRequests()
    {
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        var host = await ConfiguredHostAsync(RefreshNow.AddHours(-2), RefreshNow.AddHours(-2));
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);

        await sut.SetApiKeyAsync("second-key");
        await sut.CatalogRefreshTask;

        Assert.Empty(handler.Requests);
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        Assert.Equal(RefreshNow.AddHours(-2), sut.TextModelsFetchedAt);
    }

    [Fact]
    public async Task ManualListRefreshStartedBeforeAKeyChangeIsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = await ConfiguredHostAsync(RefreshNow, RefreshNow);
        using var sut = new OpenRouterPlugin(new HttpClient(handler), clock: new TestClock(RefreshNow));
        await sut.ActivateAsync(host);

        var refresh = sut.ExecuteSettingsActionAsync("refreshTextModels", CancellationToken.None);
        await handler.Started;
        await sut.SetApiKeyAsync("second-key");
        release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "provider/new-text");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", "{}")]
    [InlineData(HttpStatusCode.OK, """{"data":[]}""", """{"data":[]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"id":"provider/image","name":"Image","architecture":{"modality":"text->image"}}]}""", """{"data":[{"id":" "}]}""")]
    public async Task FailedOrEmptyRefreshKeepsTheCurrentLists(HttpStatusCode status, string text, string speech)
    {
        var dayOld = RefreshNow.AddDays(-1);
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, text, speech, status)));
        var host = await ConfiguredHostAsync(dayOld, dayOld);
        var writes = host.SettingWrites;
        using var sut = CreateRefreshingPlugin(handler);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
        Assert.Equal(["saved/speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(writes, host.SettingWrites);
        Assert.Equal(2, host.Warnings.Count);
        Assert.Equal(0, host.NotifyCapabilitiesChangedCount);
    }

    [Fact]
    public async Task SavedModelsSurviveListsThatLackThemAndApplyAgainOnceOffered()
    {
        var text = RefreshedText;
        var speech = """{"data":[{"id":"openai/whisper-large-v3-turbo","name":"OpenAI: Whisper Large V3 Turbo"},{"id":"a/first-speech","name":"A first speech"}]}""";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await release.Task;
            return CatalogResponse(request, text, speech);
        });
        var clock = new TestClock(RefreshNow);
        var host = await ConfiguredHostAsync(null, null, textModel: "saved/text", userSelectedTextModel: true, speechModel: "saved/speech");
        using var sut = new OpenRouterPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);

        await sut.ActivateAsync(host);
        Assert.Equal("saved/text", sut.SelectedLlmModelId);
        release.SetResult();
        await sut.CatalogRefreshTask;

        // The defaults stand in, not the alphabetically first entries; the saved choices stay.
        Assert.Equal(OpenRouterPlugin.DefaultLlmModelId, sut.SelectedLlmModelId);
        Assert.Equal(OpenRouterPlugin.DefaultTranscriptionModelId, sut.SelectedModelId);
        Assert.Equal("saved/text", SavedConfiguration(host).GetProperty("TextModel").GetString());
        Assert.Equal("saved/speech", SavedConfiguration(host).GetProperty("SpeechModel").GetString());

        text = """{"data":[{"id":"saved/text","name":"Saved text","architecture":{"modality":"text->text"}}]}""";
        speech = """{"data":[{"id":"saved/speech","name":"Saved speech"}]}""";
        clock.Now = RefreshNow.AddDays(1);
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        Assert.Equal("saved/text", sut.SelectedLlmModelId);
        Assert.Equal("saved/speech", sut.SelectedModelId);
    }

    [Fact]
    public async Task SavingTheFormKeepsASavedModelThatTheListLacks()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech)));
        var host = await ConfiguredHostAsync(null, null, textModel: "saved/text", userSelectedTextModel: true, speechModel: "saved/speech");
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;

        // The page sends every field as shown, including the defaults that stand in for the saved models.
        var shown = sut.TextSettings.Where(field => field.Id != "configurationProfile").ToDictionary(field => field.Id, field => field.Value);
        shown["llmTemperatureMode"] = "custom";
        await sut.SaveProfileSettingsAsync("openrouter", shown, null, CancellationToken.None);

        var saved = SavedConfiguration(host);
        Assert.Equal("saved/text", saved.GetProperty("TextModel").GetString());
        Assert.True(saved.GetProperty("UserSelectedTextModel").GetBoolean());
        Assert.Equal("saved/speech", saved.GetProperty("SpeechModel").GetString());
        Assert.Equal("custom", sut.TemperatureMode);
    }

    [Fact]
    public async Task BackgroundRefreshLeavesAnOpenDraftForTheSave()
    {
        var releaseBackground = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            if (request.Headers.Authorization?.Parameter == "draft-key")
            {
                return CatalogResponse(request,
                    """{"data":[{"id":"draft/text","name":"Draft text","architecture":{"modality":"text->text"}}]}""",
                    """{"data":[{"id":"draft/speech","name":"Draft speech"}]}""");
            }

            await releaseBackground.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = await ConfiguredHostAsync(null, null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await handler.Started;

        var draft = await sut.ExecuteProfileActionAsync("openrouter", "refreshModels", new Dictionary<string, string>(), "draft-key", CancellationToken.None);
        releaseBackground.SetResult();
        await sut.CatalogRefreshTask;

        Assert.True(draft.HasPendingChanges);
        Assert.Contains(sut.SupportedModels, model => model.Id == "provider/new-text");
        Assert.Contains(sut.TextSettings.Single(field => field.Id == "selectedLlmModel").Choices, choice => choice.Value == "draft/text");

        await sut.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string> { ["selectedLlmModel"] = "draft/text" }, null, CancellationToken.None);

        Assert.Equal("draft/text", sut.SelectedLlmModelId);
        Assert.DoesNotContain(sut.SupportedModels, model => model.Id == "provider/new-text");
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
    }

    [Fact]
    public async Task BackgroundRefreshDuringASaveKeepsBothChanges()
    {
        var releaseBackground = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CatalogHandler(async request =>
        {
            await releaseBackground.Task;
            return CatalogResponse(request, RefreshedText, RefreshedSpeech);
        });
        var host = await ConfiguredHostAsync(null, null);
        using var sut = CreateRefreshingPlugin(handler);
        await sut.ActivateAsync(host);
        await handler.Started;
        var secretWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecretWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SecretWriteGate = async () =>
        {
            secretWriteStarted.TrySetResult();
            await releaseSecretWrite.Task;
        };

        // Re-entering the same key keeps the refresh running while the save waits for the secret store.
        var save = sut.SaveProfileSettingsAsync("openrouter",
            new Dictionary<string, string> { ["llmTemperatureMode"] = "custom" }, "openrouter-key", CancellationToken.None);
        await secretWriteStarted.Task;
        releaseBackground.SetResult();
        await sut.CatalogRefreshTask;
        releaseSecretWrite.SetResult();
        await save;

        Assert.Equal("custom", sut.TemperatureMode);
        Assert.Contains(sut.SupportedModels, model => model.Id == "provider/new-text");
        var saved = SavedConfiguration(host);
        Assert.Equal("custom", saved.GetProperty("TemperatureMode").GetString());
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
    }

    [Fact]
    public async Task RefreshActionsRecordTheFetchDates()
    {
        var handler = new CatalogHandler(request => Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech)));
        var clock = new TestClock(RefreshNow);
        var dayOld = RefreshNow.AddDays(-1);
        var host = await ConfiguredHostAsync(dayOld, dayOld);
        using var sut = new OpenRouterPlugin(new HttpClient(handler), clock: clock);
        await sut.ActivateAsync(host);

        await sut.ExecuteSettingsActionAsync("refreshTextModels", CancellationToken.None);

        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(dayOld, SavedDate(host, "SpeechModelsFetchedAt"));

        clock.Now = RefreshNow.AddHours(1);
        await sut.ExecuteSettingsActionAsync("refreshModels", CancellationToken.None);
        await sut.SaveProfileSettingsAsync("openrouter", new Dictionary<string, string>(), null, CancellationToken.None);

        Assert.Equal(RefreshNow.AddHours(1), SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(RefreshNow.AddHours(1), SavedDate(host, "SpeechModelsFetchedAt"));
    }

    [Fact]
    public async Task Recheck_ReloadsListsThatAgeWhileThePluginStaysActive()
    {
        var clock = new RecheckTestClock(RefreshNow);
        // One answer per list. A request beyond the queued answers finds the queue empty and fails.
        var answers = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.OK]);
        var handler = new CatalogHandler(request =>
            Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech, answers.Dequeue())));
        var host = await ConfiguredHostAsync(RefreshNow, RefreshNow);
        using var sut = new OpenRouterPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;
        Assert.Empty(handler.Requests);

        clock.Advance(TimeSpan.FromHours(24));
        await sut.CatalogRefreshTask;

        var dayLater = RefreshNow.AddHours(24);
        Assert.Equal(["https://openrouter.ai/api/v1/models", "https://openrouter.ai/api/v1/models?output_modalities=transcription"], handler.Requests);
        Assert.Equal([OpenRouterPlugin.DefaultLlmModelId, "provider/new-text"], sut.SupportedModels.Select(m => m.Id).ToArray());
        Assert.Equal(["provider/new-speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal([OpenRouterPlugin.DefaultLlmModelId, "provider/new-text"], SavedModelIds(host, "TextModels"));
        Assert.Equal(["provider/new-speech"], SavedModelIds(host, "SpeechModels"));
        Assert.Equal(dayLater, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Equal(dayLater, SavedDate(host, "SpeechModelsFetchedAt"));
    }

    [Fact]
    public async Task Recheck_StopsWhenThePluginDeactivates()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var handler = new CatalogHandler(_ => throw new InvalidOperationException("Unexpected model refresh."));
        using var sut = new OpenRouterPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(await ConfiguredHostAsync(RefreshNow, RefreshNow));
        Assert.Equal(1, clock.ActiveTimers);

        await sut.DeactivateAsync();
        clock.Advance(TimeSpan.FromHours(24));
        await sut.CatalogRefreshTask;

        Assert.Equal(0, clock.ActiveTimers);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Recheck_RetriesAFailedRefreshAnHourLater()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var answers = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        var handler = new CatalogHandler(request =>
            Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech, answers.Dequeue())));
        // Only the transcription list is due, so each refresh is a single request.
        var host = await ConfiguredHostAsync(RefreshNow, RefreshNow.AddDays(-1));
        using var sut = new OpenRouterPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        const string speechList = "https://openrouter.ai/api/v1/models?output_modalities=transcription";

        await sut.ActivateAsync(host);
        await sut.CatalogRefreshTask;
        Assert.Equal([speechList], handler.Requests);
        Assert.Equal(["saved/speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());

        clock.Advance(ModelCatalogAutoRefresh.RecheckInterval);
        await sut.CatalogRefreshTask;

        var hourLater = RefreshNow.Add(ModelCatalogAutoRefresh.RecheckInterval);
        Assert.Equal([speechList, speechList], handler.Requests);
        Assert.Equal(["provider/new-speech"], sut.TranscriptionModels.Select(m => m.Id).ToArray());
        Assert.Equal(hourLater, SavedDate(host, "SpeechModelsFetchedAt"));
        Assert.Equal(RefreshNow, SavedDate(host, "TextModelsFetchedAt"));
        Assert.Contains(sut.SupportedModels, model => model.Id == "saved/text");
    }

    // The hourly recheck runs on a timer thread. While a key change waits in its secret write, the old key is still set,
    // so a recheck must not start a refresh; the key change refreshes with the new key once it is in place.
    [Fact]
    public async Task Recheck_DuringAKeyChangeSendsNoRequestWithTheOldKey()
    {
        var clock = new RecheckTestClock(RefreshNow);
        var keys = new List<string?>();
        var handler = new CatalogHandler(request =>
        {
            lock (keys) keys.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(CatalogResponse(request, RefreshedText, RefreshedSpeech));
        });
        var host = await ConfiguredHostAsync(RefreshNow, RefreshNow);
        using var sut = new OpenRouterPlugin(new HttpClient(handler), refreshModelsAutomatically: true, clock: clock);
        await sut.ActivateAsync(host);
        var secretWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecretWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SecretWriteGate = async () =>
        {
            secretWriteStarted.TrySetResult();
            await releaseSecretWrite.Task;
        };

        var keyChange = sut.SetApiKeyAsync("second-key");
        await secretWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromHours(24));
        await sut.CatalogRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));
        lock (keys) Assert.Empty(keys);

        releaseSecretWrite.SetResult();
        await keyChange.WaitAsync(TimeSpan.FromSeconds(10));
        await sut.CatalogRefreshTask.WaitAsync(TimeSpan.FromSeconds(10));

        lock (keys) Assert.Equal(["second-key", "second-key"], keys);
        Assert.Contains(sut.SupportedModels, model => model.Id == "provider/new-text");
    }

    private static OpenRouterPlugin CreateRefreshingPlugin(HttpMessageHandler handler) =>
        new(new HttpClient(handler), refreshModelsAutomatically: true, clock: new TestClock(RefreshNow));

    private static async Task<TestPluginHostServices> ConfiguredHostAsync(DateTimeOffset? textModelsFetchedAt,
        DateTimeOffset? speechModelsFetchedAt, string? textModel = null, bool userSelectedTextModel = false, string? speechModel = null)
    {
        var host = new TestPluginHostServices();
        host.SetSetting("configuration", new
        {
            SpeechModel = speechModel, TextModel = textModel, UserSelectedTextModel = userSelectedTextModel,
            TemperatureMode = "providerDefault", Temperature = 0.3,
            SpeechModels = new[] { new OpenRouterFetchedModel("saved/speech", "Saved speech", "0", "0") },
            TextModels = new[] { new OpenRouterFetchedModel("saved/text", "Saved text", "0", "0") },
            SecretName = "saved-key",
            SpeechModelsFetchedAt = speechModelsFetchedAt,
            TextModelsFetchedAt = textModelsFetchedAt,
        });
        await host.StoreSecretAsync("saved-key", "openrouter-key");
        return host;
    }

    private static JsonElement SavedConfiguration(TestPluginHostServices host) =>
        host.GetSetting<JsonElement>("configuration");

    private static DateTimeOffset? SavedDate(TestPluginHostServices host, string name) =>
        SavedConfiguration(host).TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetDateTimeOffset()
            : null;

    private static string[] SavedModelIds(TestPluginHostServices host, string list) =>
        SavedConfiguration(host).GetProperty(list).EnumerateArray().Select(model => model.GetProperty("Id").GetString()!).ToArray();

    private static HttpResponseMessage CatalogResponse(HttpRequestMessage request, string text, string speech,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("transcription", StringComparison.Ordinal) ? speech : text,
                Encoding.UTF8, "application/json"),
        };

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers catalog requests and records which lists were requested.</summary>
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
