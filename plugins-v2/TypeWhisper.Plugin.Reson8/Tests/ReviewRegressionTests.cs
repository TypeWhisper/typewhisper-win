using System.Net;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
    [Theory]
    [InlineData("selectedModel")]
    [InlineData("fetchedCustomModels")]
    public async Task FailedModelPersistencePreservesCatalogAndSelection(string failingSetting)
    {
        using var plugin = new Reson8Plugin(); var host = new TestPluginHostServices();
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        host.FailSettingName = failingSetting;
        Assert.Throws<IOException>(() => plugin.SetFetchedCustomModels([]));
        Assert.Equal("custom", plugin.SelectedModelId); Assert.Single(plugin.FetchedCustomModels);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("custom", plugin.SelectedModelId); Assert.Single(plugin.FetchedCustomModels);
    }

    [Theory]
    [InlineData("selectedModel")]
    [InlineData("fetchedCustomModels")]
    [InlineData("customBaseURL")]
    public async Task FailedServerChangePreservesEndpointAndModels(string failingSetting)
    {
        using var plugin = new Reson8Plugin(); var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture"; await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        host.FailSettingName = failingSetting;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync("baseUrl", "https://new.example.test", default));
        Assert.Equal(Reson8Plugin.DefaultBaseUrl, plugin.CustomBaseUrl); Assert.Equal("custom", plugin.SelectedModelId);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(Reson8Plugin.DefaultBaseUrl, plugin.CustomBaseUrl);
        Assert.Equal("custom", plugin.SelectedModelId); Assert.Equal("custom", Assert.Single(plugin.FetchedCustomModels).Id);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("")]
    public async Task FailedSecretWriteRestoresModelStateAcrossRestart(string replacement)
    {
        using var plugin = new Reson8Plugin(); var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "old-key"; await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        host.FailSecretWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync(replacement));
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("old-key", plugin.ApiKey); Assert.Equal("custom", plugin.SelectedModelId);
        Assert.Equal("custom", Assert.Single(plugin.FetchedCustomModels).Id);
    }

    [Theory]
    [InlineData(20, 3)]
    [InlineData(22, 2)]
    [InlineData(24, 44100)]
    [InlineData(34, 32)]
    [InlineData(40, 100)]
    public async Task UnsupportedOrTruncatedWavNeverUploads(int offset, int value)
    {
        var requests = 0;
        using var client = new HttpClient(new CapturingHandler((_, _) => { requests++; return JsonResponse("{}"); }));
        using var plugin = new Reson8Plugin(client); var host = new TestPluginHostServices();
        host.Secrets["api-key"] = "fixture"; await plugin.ActivateAsync(host);
        var wav = BuildPcm16Wav(new byte[8]);
        var bytes = offset is 24 or 40 ? BitConverter.GetBytes(value) : BitConverter.GetBytes((short)value);
        bytes.CopyTo(wav, offset);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribeAsync(wav, "de", false, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribeStreamingAsync(wav, "de", false, null, _ => true, default));
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("baseUrl", "relative/path")]
    [InlineData("baseUrl", "ftp://example.test")]
    [InlineData("baseUrl", "https://example.test?key=value")]
    [InlineData("baseUrl", "https://example.test#fragment")]
    [InlineData("baseUrl", "https://user:password@example.test")]
    [InlineData("authHeader", "X API Key")]
    [InlineData("authHeader", "Authorization:")]
    [InlineData("authHeader", "X-Äpi-Key")]
    public async Task InvalidConnectionSettingsLeaveSavedConfigurationUntouched(string id, string value)
    {
        using var plugin = new Reson8Plugin(); var host = new TestPluginHostServices();
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync(id, value, default));
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(Reson8Plugin.DefaultBaseUrl, plugin.CustomBaseUrl);
        Assert.Equal(Reson8Plugin.DefaultAuthHeader, plugin.CustomAuthHeader);
        Assert.Equal("custom", plugin.SelectedModelId);
    }

    [Theory]
    [InlineData("selectedModel")]
    [InlineData("fetchedCustomModels")]
    public async Task FailedDependentSettingsCannotCommitNewCredential(string failingSetting)
    {
        using var plugin = new Reson8Plugin();
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "old-key";
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        host.FailSettingName = failingSetting;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("new-key"));
        Assert.Equal("old-key", plugin.ApiKey); Assert.Equal("old-key", host.Secrets["api-key"]);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("old-key", plugin.ApiKey);
    }

    [Theory]
    [InlineData("https://proxy.example.test/reson8", true)]
    [InlineData(" https://api.reson8.dev/ ", false)]
    public async Task ServerChangesInvalidateModelsOnlyForDifferentEndpoints(string endpoint, bool changed)
    {
        using var plugin = new Reson8Plugin();
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "first";
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        var notifications = host.NotifyCapabilitiesChangedCount;
        await plugin.SaveTextSettingAsync("baseUrl", endpoint, default);
        Assert.Equal(notifications + (changed ? 1 : 0), host.NotifyCapabilitiesChangedCount);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal(changed ? Reson8Plugin.DefaultModelId : "custom", plugin.SelectedModelId);
        Assert.Equal(changed ? 0 : 1, plugin.FetchedCustomModels.Count);
    }

    [Theory]
    [InlineData(401, "[]")]
    [InlineData(429, "[]")]
    [InlineData(500, "[]")]
    [InlineData(200, "invalid-json")]
    public async Task FailedRefreshPreservesCustomModelSelection(int status, string json)
    {
        using var client = new HttpClient(new CapturingHandler((_, _) => JsonResponse(json, (HttpStatusCode)status)));
        using var plugin = new Reson8Plugin(client);
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "first";
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        Assert.NotNull(await Record.ExceptionAsync(() => plugin.ExecuteSettingsActionAsync("refresh", default)));
        Assert.Equal("custom", plugin.SelectedModelId);
        Assert.Equal("custom", Assert.Single(plugin.FetchedCustomModels).Id);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("custom", plugin.SelectedModelId);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("")]
    public async Task CredentialChangesClearAccountModels(string replacement)
    {
        using var plugin = new Reson8Plugin();
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "first";
        await plugin.ActivateAsync(host);
        plugin.SetFetchedCustomModels([new("custom", "Custom", null, null)]); plugin.SelectModel("custom");
        await plugin.SetApiKeyAsync("first"); Assert.Equal("custom", plugin.SelectedModelId);
        await plugin.SetApiKeyAsync(replacement);
        Assert.Empty(plugin.FetchedCustomModels); Assert.Equal(Reson8Plugin.DefaultModelId, plugin.SelectedModelId);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Empty(plugin.FetchedCustomModels); Assert.Equal(Reson8Plugin.DefaultModelId, plugin.SelectedModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressCancellationStopsUploadAndNeverFallsBack(bool duringFinalize)
    {
        var requests = 0;
        using var client = new HttpClient(new CapturingHandler((_, _) => { requests++; return JsonResponse("{}"); }));
        var session = new ProgressSession(duringFinalize);
        using var plugin = new Reson8Plugin(client, (_, _) => Task.FromResult<IStreamingSession>(session));
        var host = new TestPluginHostServices(); host.Secrets["api-key"] = "fixture";
        await plugin.ActivateAsync(host);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribeStreamingAsync(
            BuildPcm16Wav(new byte[32768]), "de", false, null, _ => false, default));
        Assert.Equal(duringFinalize ? 4 : 1, session.Sends);
        Assert.Equal(duringFinalize, session.Finalized);
        Assert.True(session.Disposed); Assert.Equal(0, requests);
    }

    [Fact]
    public void RealtimePreservesProxyPathAndPort()
    {
        var uri = Reson8StreamingSession.BuildRealtimeUri("https://proxy.example.test:8443/reson8/", null, "de");
        Assert.Equal("wss", uri.Scheme); Assert.Equal(8443, uri.Port);
        Assert.Equal("/reson8/v1/speech-to-text/realtime", uri.AbsolutePath);
    }

    private sealed class ProgressSession(bool duringFinalize) : IStreamingSession
    {
        public int Sends { get; private set; }
        public bool Finalized { get; private set; }
        public bool Disposed { get; private set; }
        public event Action<StreamingTranscriptEvent>? TranscriptReceived;
        public Task SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        { Sends++; if (!duringFinalize) TranscriptReceived?.Invoke(new("Partial", false)); return Task.CompletedTask; }
        public Task FinalizeAsync(CancellationToken ct)
        { Finalized = true; TranscriptReceived?.Invoke(new("Final", true)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
