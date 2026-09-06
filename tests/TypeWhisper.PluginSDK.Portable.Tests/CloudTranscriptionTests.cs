using System.Net;
using System.Text;
using Moq;
using TypeWhisper.Plugin.Groq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.WinUI;
using Xunit;

public sealed class CloudTranscriptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "groq-runtime-" + Guid.NewGuid());
    private readonly Secrets _secrets = new();
    private readonly List<(HttpMethod Method, string Uri, string? Key, string? Body)> _requests = [];
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _respond;
    private VocabularyHostServices Host => new(_root, secrets: _secrets);
    private CloudTranscriptionPlugin Create() => new(Host, async () =>
    {
        var plugin = new GroqPlugin(new HttpClient(new Handler(async (request, ct) =>
        {
            _requests.Add((request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.Parameter,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return _respond is not null ? await _respond(request, ct) : Json("{\"text\":\"Guten Morgen\",\"language\":\"german\",\"duration\":1}");
        })));
        await plugin.ActivateAsync(Host);
        return new(plugin, plugin, new Lifetime(plugin));
    });

    [Fact]
    public async Task FirstLaunchDoesNotLoadOrContactGroqAndMissingKeyBlocksAudio()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.False(runtime.Enabled); Assert.Empty(_requests);
        await runtime.SetEnabledAsync(true);
        Assert.True(runtime.Enabled); Assert.False(runtime.Ready); Assert.Equal(2, runtime.Models.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DecodeAsync([0.1f]));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("whisper-large-v3")]
    [InlineData("whisper-large-v3-turbo")]
    public async Task SavedModelKeyAndLanguageSurviveRestartAndReachActualHttpRequest(string model)
    {
        await using (var runtime = Create())
        {
            await runtime.SetEnabledAsync(true); await runtime.SaveKeyAsync("  test-key  ");
            await runtime.SelectModelAsync(model); runtime.SelectLanguage("de");
        }
        await using var restarted = Create(); await restarted.InitializeAsync();
        Assert.True(restarted.Ready); Assert.Equal(model, restarted.ModelId); Assert.Equal("de", restarted.Language);
        Assert.DoesNotContain("test-key", File.ReadAllText(Path.Combine(_root, "settings.json")));
        var result = await restarted.DecodeAsync([0, 0.5f, -0.5f]);
        Assert.Equal("Guten Morgen", result.Text);
        var request = Assert.Single(_requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", request.Uri);
        Assert.Equal("test-key", request.Key);
        Assert.Contains(model, request.Body); Assert.Contains("\r\nde\r\n", request.Body);
        Assert.Contains("audio/wav", request.Body); Assert.Contains("RIFF", request.Body);
        Assert.Empty(result.Timings);
    }

    [Fact]
    public async Task SavingKeyEnablesPluginWithoutASeparateEnableStepOrNetworkRequest()
    {
        await using var runtime = Create(); await runtime.InitializeAsync();
        Assert.False(runtime.Enabled);
        await runtime.SaveKeyAsync("key");
        Assert.True(runtime.Enabled); Assert.True(runtime.Ready); Assert.Empty(_requests);
        Assert.True(Host.GetSetting<bool>("Enabled"));
    }

    [Fact]
    public async Task AutoLanguageIsOmittedAndConnectionCheckUploadsNoAudio()
    {
        await using var runtime = Create(); await runtime.SetEnabledAsync(true); await runtime.SaveKeyAsync("key");
        await runtime.ValidateAsync();
        var check = Assert.Single(_requests); Assert.Equal(HttpMethod.Get, check.Method); Assert.Null(check.Body);
        Assert.EndsWith("/v1/models", check.Uri);
        await runtime.DecodeAsync([0.1f]);
        Assert.DoesNotContain("name=language", _requests[1].Body);
        Assert.DoesNotContain("name=\"language\"", _requests[1].Body);
    }

    [Fact]
    public async Task RemovingKeyAndDisablingPreventFurtherUploads()
    {
        await using var runtime = Create(); await runtime.SetEnabledAsync(true); await runtime.SaveKeyAsync("key");
        await runtime.SaveKeyAsync(""); Assert.False(runtime.Ready); Assert.Empty(_secrets.Values);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DecodeAsync([0.1f]));
        await runtime.SetEnabledAsync(false); Assert.False(runtime.Enabled);
        await using var restarted = Create(); await restarted.InitializeAsync(); Assert.False(restarted.Enabled);
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(401, "API key")]
    [InlineData(429, "rate limit")]
    [InlineData(500, "could not complete")]
    public async Task HttpErrorsAreActionableAndDoNotEchoResponseBodies(int status, string message)
    {
        _respond = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("sensitive-provider-response") });
        await using var runtime = Create(); await runtime.SetEnabledAsync(true); await runtime.SaveKeyAsync("key");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.DecodeAsync([0.1f]));
        Assert.Contains(message, error.Message); Assert.DoesNotContain("sensitive", error.ToString());
        _respond = null;
        Assert.Equal("Guten Morgen", (await runtime.DecodeAsync([0.1f])).Text);
        Assert.Null(runtime.Error);
    }

    [Fact]
    public async Task ShutdownCancelsHttpAndDrainsBeforeDisposalAndConcurrentMutationIsRejected()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _respond = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return Json("{}"); };
        var runtime = Create(); await runtime.SetEnabledAsync(true); await runtime.SaveKeyAsync("key");
        var decode = runtime.DecodeAsync([0.1f]); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetEnabledAsync(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SaveKeyAsync("replacement"));
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => decode);
        Assert.False(runtime.Enabled); Assert.Equal("key", _secrets.Values["api-key"]);
    }

    [Fact]
    public async Task FailedSecretSavePreservesWorkingKeyAndConfiguration()
    {
        using var plugin = new GroqPlugin(); await plugin.ActivateAsync(Host); await plugin.SetApiKeyAsync("original");
        _secrets.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("replacement"));
        Assert.True(plugin.IsConfigured); Assert.Equal("original", plugin.ApiKey);
    }

    [Fact]
    public async Task FailedModelPersistenceDoesNotChangeRuntimeModel()
    {
        var host = new Mock<IPluginHostServices>();
        host.Setup(h => h.LoadSecretAsync("api-key")).ReturnsAsync("key");
        host.Setup(h => h.SetSetting("selectedModel", "whisper-large-v3-turbo")).Throws<IOException>();
        using var plugin = new GroqPlugin(); await plugin.ActivateAsync(host.Object);
        Assert.Throws<IOException>(() => plugin.SelectModel("whisper-large-v3-turbo"));
        Assert.Equal("whisper-large-v3", plugin.SelectedModelId);
    }

    [Fact]
    public void WavContainsCorrectHeaderAndClampedPcm16Samples()
    {
        var wav = CloudTranscriptionPlugin.EncodeWav([-2, -0.5f, 0, 0.5f, 2]);
        using var reader = new BinaryReader(new MemoryStream(wav));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4))); Assert.Equal(46, reader.ReadInt32());
        reader.BaseStream.Position = 22; Assert.Equal(1, reader.ReadInt16()); Assert.Equal(16000, reader.ReadInt32());
        reader.BaseStream.Position = 40; Assert.Equal(10, reader.ReadInt32());
        Assert.Equal(new short[] { -32768, -16384, 0, 16384, 32767 }, Enumerable.Range(0, 5).Select(_ => reader.ReadInt16()));
        Assert.Throws<ArgumentException>(() => CloudTranscriptionPlugin.EncodeWav([float.NaN]));
        Assert.Throws<ArgumentException>(() => CloudTranscriptionPlugin.EncodeWav([]));
        Assert.Throws<PluginRequestException>(() => CloudTranscriptionPlugin.EncodeWav(new float[12_500_000]));
    }

    [Fact]
    public async Task PortablePackageLoadsWithoutWpfAndExposesHostRenderedConfiguration()
    {
        var directory = Path.Combine(_root, "package"); Directory.CreateDirectory(directory);
        File.Copy(typeof(GroqPlugin).Assembly.Location, Path.Combine(directory, "TypeWhisper.Plugin.Groq.dll"));
        File.WriteAllText(Path.Combine(directory, "manifest.json"), """
            {"id":"com.typewhisper.groq","name":"Groq","version":"1.0.6","minHostVersion":"1.1.0","assemblyName":"TypeWhisper.Plugin.Groq.dll","pluginClass":"TypeWhisper.Plugin.Groq.GroqPlugin"}
            """);
        await using var package = await PortablePluginPackage.LoadAsync(directory, Host, LocalCtcVocabulary.HostVersion);
        Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(package.Plugin);
        Assert.IsAssignableFrom<IApiKeyPlugin>(package.Plugin);
        Assert.DoesNotContain(package.Plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "NAudio");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => action(request, ct); }
    private sealed class Lifetime(GroqPlugin plugin) : IAsyncDisposable
    { public async ValueTask DisposeAsync() { await plugin.DeactivateAsync(); plugin.Dispose(); } }
    private sealed class Secrets : IPluginSecretStore
    {
        internal Dictionary<string, string> Values = [];
        internal bool FailWrites;
        public Task StoreAsync(string key, string value) { if (FailWrites) throw new IOException(); Values[key] = value; return Task.CompletedTask; }
        public Task<string?> LoadAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task DeleteAsync(string key) { Values.Remove(key); return Task.CompletedTask; }
    }
    public void Dispose()
    {
        // Collectible package assemblies remain file-mapped until their load context is collected.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
