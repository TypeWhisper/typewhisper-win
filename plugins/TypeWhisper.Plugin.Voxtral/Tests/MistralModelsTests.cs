using System.Net;
using System.Text.Json;
using TypeWhisper.Plugin.Voxtral;
using TypeWhisper.PluginSDK;

public sealed partial class ProviderTests
{
    [Fact]
    public async Task TextOnlyAccountRetainsChatAndCredentialsWithoutDictationReadiness()
    {
        using var http = new HttpClient(new Handler((_, _) => Json("""{"data":[{"id":"mistral-small-latest","capabilities":{"completion_chat":true}}]}""")));
        using var plugin = new VoxtralPlugin(http); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.True(((IApiKeyPlugin)plugin).IsConfigured); Assert.True(plugin.IsAvailable);
        Assert.False(((ITranscriptionEnginePlugin)plugin).IsConfigured); Assert.Null(plugin.SelectedModelId);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.True(plugin.IsAvailable); Assert.False(((ITranscriptionEnginePlugin)plugin).IsConfigured);
    }

    [Fact]
    public async Task ContendedKeyAndModelSavesDoNotCaptureUiContext()
    {
        using var plugin = new VoxtralPlugin(); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StoreSecretDelay = release.Task;
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task keySave, modelSave;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            keySave = plugin.SetApiKeyAsync("replacement-key");
            modelSave = plugin.SaveTextSettingAsync("model", VoxtralPlugin.RealtimeModel, default);
            Assert.False(keySave.IsCompleted); Assert.False(modelSave.IsCompleted);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); release.TrySetResult(); }
        await Task.WhenAll(keySave, modelSave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.Posts);
        Assert.Equal(VoxtralPlugin.RealtimeModel, plugin.SelectedModelId);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    private const string CatalogResponse = """
        {"data":[
          {"id":"mistral-small-latest","capabilities":{"completion_chat":true}},
          {"id":"codestral-latest","capabilities":{"completion_chat":true}},
          {"id":"codestral-latest","capabilities":{"completion_chat":true}},
          {"id":"voxtral-mini-latest","capabilities":{"audio_transcription":true}},
          {"id":"realtime","capabilities":{"audio_transcription_realtime":true}},
          {"id":"speech","capabilities":{"completion_chat":true,"audio_speech":true}},
          {"id":"embedding","capabilities":{"completion_chat":false}},
          {"id":"retired","archived":true,"capabilities":{"completion_chat":true}}
        ]}
        """;

    [Fact]
    public async Task DiscoveryFiltersCapabilitiesAndPersistsAcrossRestart()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("https://api.mistral.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", request.Headers.Authorization!.Parameter);
            return Json(CatalogResponse);
        }));
        using var plugin = new VoxtralPlugin(http);
        var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        _ = plugin.TextSettings; _ = plugin.SettingsActions;
        Assert.Equal(0, calls);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.Equal(new[] { "codestral-latest", "mistral-small-latest" }, plugin.SupportedModels.Select(m => m.Id));
        Assert.Equal(new[] { "realtime", "voxtral-mini-latest" }, plugin.TranscriptionModels.Select(m => m.Id));
        await plugin.SaveTextSettingAsync("llmModel", "codestral-latest", default);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(2, plugin.SupportedModels.Count);
        Assert.Equal("codestral-latest", plugin.TextSettings.Single(s => s.Id == "llmModel").Value);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"data\":[{\"id\":\"incomplete\"}]}")]
    [InlineData("{\"data\":null}")]
    [InlineData("not-json")]
    public async Task FailedDiscoveryKeepsSavedCatalog(string invalid)
    {
        var body = CatalogResponse;
        using var http = new HttpClient(new Handler((_, _) => Json(body)));
        using var plugin = new VoxtralPlugin(http); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        var previous = JsonSerializer.Serialize(host.Settings);
        body = invalid;
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.Equal(previous, JsonSerializer.Serialize(host.Settings));
        Assert.Equal(2, plugin.SupportedModels.Count);
    }

    [Fact]
    public async Task FailedCatalogSaveKeepsPublishedModelsAndKeyChangeResetsCatalog()
    {
        using var http = new HttpClient(new Handler((_, _) => Json(CatalogResponse)));
        using var plugin = new VoxtralPlugin(http); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        host.FailSetting = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
        Assert.Single(plugin.SupportedModels);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        await plugin.SetApiKeyAsync("fixture-key");
        Assert.Equal(2, plugin.SupportedModels.Count);
        await plugin.SetApiKeyAsync("new-key");
        Assert.Single(plugin.SupportedModels);
        Assert.Equal("Mistral", plugin.PluginName);
        Assert.Equal("com.typewhisper.voxtral", plugin.PluginId);
    }

    [Fact]
    public async Task EmptyAccountCatalogDoesNotReintroduceUnavailableModels()
    {
        using var http = new HttpClient(new Handler((_, _) => Json("{\"data\":[]}")));
        using var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        Assert.Empty(plugin.SupportedModels); Assert.Empty(plugin.TranscriptionModels);
        Assert.Null(plugin.SelectedModelId); Assert.False(plugin.IsAvailable);
        Assert.True(((IApiKeyPlugin)plugin).IsConfigured);
        Assert.False(((ITranscriptionEnginePlugin)plugin).IsConfigured);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("llmModel", "invented", default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("model", "invented", default));
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.TranscribeAsync(Audio(), "de", false, null, default));
    }

    [Fact]
    public async Task KeyChangedDuringDiscoveryCannotPublishOldAccountCatalog()
    {
        VoxtralPlugin? plugin = null;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            plugin!.SetApiKeyAsync("replacement").GetAwaiter().GetResult();
            return Json(CatalogResponse);
        }));
        using (plugin = new VoxtralPlugin(http))
        {
            await plugin.ActivateAsync(new Host()); await Configure(plugin);
            await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", default));
            Assert.Single(plugin.SupportedModels);
        }
    }

    [Fact]
    public async Task CanceledDiscoveryDoesNotSendRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected request")));
        using var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.ExecuteSettingsActionAsync("refreshModels", new(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatUsesSameKeySelectedModelAndOptionalTemperature(bool custom)
    {
        using var http = new HttpClient(new Handler((request, body) =>
        {
            if (request.Method == HttpMethod.Get) return Json(CatalogResponse);
            Assert.Equal("https://api.mistral.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-key", request.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(body!);
            Assert.Equal("codestral-latest", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("Grüße", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.Equal(custom, json.RootElement.TryGetProperty("temperature", out var temperature));
            if (custom) Assert.Equal(0.2, temperature.GetDouble());
            return Json("""{"choices":[{"finish_reason":"stop","message":{"content":"Grüße!"}}]}""");
        }));
        using var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await plugin.ExecuteSettingsActionAsync("refreshModels", default);
        await plugin.SaveTextSettingAsync("llmModel", "codestral-latest", default);
        if (custom)
        {
            await plugin.SaveTextSettingAsync("temperatureMode", "custom", default);
            await plugin.SaveTextSettingAsync("temperature", "0.2", default);
        }
        Assert.Equal("Grüße!", await plugin.ProcessAsync("Correct spelling.", "Grüße", "", default));
    }

    [Fact]
    public async Task ReasoningChunksStayOutOfFinalText()
    {
        using var http = new HttpClient(new Handler((_, _) => Json("""
            {"choices":[{"finish_reason":"stop","message":{"content":[
             {"type":"thinking","thinking":[{"type":"text","text":"Private reasoning"}]},
             {"type":"text","text":"Hallo "},{"type":"text","text":"Welt!"}]}}]}
            """)));
        using var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        Assert.Equal("Hallo Welt!", await plugin.ProcessAsync("Correct", "hallo welt", "", default));
    }

    [Theory]
    [InlineData("length")]
    [InlineData("tool_calls")]
    public async Task IncompleteChatIsRejected(string reason)
    {
        using var http = new HttpClient(new Handler((_, _) => Json(JsonSerializer.Serialize(new
        { choices = new[] { new { finish_reason = reason, message = new { content = "Partial" } } } }))));
        using var plugin = new VoxtralPlugin(http);
        await plugin.ActivateAsync(new Host()); await Configure(plugin);
        await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ProcessAsync("Correct", "text", "", default));
    }
}
