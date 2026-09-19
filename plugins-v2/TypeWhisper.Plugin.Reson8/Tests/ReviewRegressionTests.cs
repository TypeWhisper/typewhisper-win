using System.Net;
using TypeWhisper.Plugin.Reson8;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class Reson8PluginTests
{
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
