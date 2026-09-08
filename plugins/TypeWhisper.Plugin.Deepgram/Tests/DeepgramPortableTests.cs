using System.Net;
using TypeWhisper.Plugin.Deepgram;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed class DeepgramPortableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deepgram-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Secrets _secrets = new();
    private VocabularyHostServices Host => new(_root, secrets: _secrets);

    [Fact]
    public async Task ConfigurationAndModelSurviveReactivationWithoutNetwork()
    {
        using var plugin = new DeepgramPlugin(new HttpClient(new Handler((_, _) => throw new Exception("Unexpected network"))));
        await plugin.ActivateAsync(Host);
        Assert.Equal("nova-3", plugin.SelectedModelId);
        await plugin.SetApiKeyAsync(" test-key ");
        plugin.SelectModel("nova-2");
        await plugin.DeactivateAsync();
        Assert.False(plugin.IsConfigured);
        await plugin.ActivateAsync(Host);
        Assert.True(plugin.IsConfigured);
        Assert.Equal("nova-2", plugin.SelectedModelId);
        _secrets.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SetApiKeyAsync("replacement"));
        Assert.Equal("test-key", _secrets.Value);
        Assert.True(plugin.IsConfigured);
        _secrets.FailWrites = false;
        await plugin.SetApiKeyAsync("");
        Assert.False(plugin.IsConfigured);
        Assert.Null(_secrets.Value);
    }

    [Fact]
    public async Task BatchRequestPreservesAudioLanguageAndResult()
    {
        using var plugin = new DeepgramPlugin(new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Token", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Contains("model=nova-3", request.RequestUri!.Query);
            Assert.Contains("language=de", request.RequestUri.Query);
            Assert.Equal("audio/wav", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal(new byte[] { 1, 2, 3 }, await request.Content.ReadAsByteArrayAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"metadata":{"duration":1.25},"results":{"channels":[{"detected_language":"de","alternatives":[{"transcript":"Guten Tag."}]}]}}
                """) };
        })));
        await plugin.ActivateAsync(Host);
        await plugin.SetApiKeyAsync("test-key");
        var result = await plugin.TranscribeAsync([1, 2, 3], "de", false, null, default);
        Assert.Equal("Guten Tag.", result.Text);
        Assert.Equal("de", result.DetectedLanguage);
        await Assert.ThrowsAsync<NotSupportedException>(() => plugin.TranscribeAsync([1], null, true, null, default));
    }

    [Theory]
    [InlineData(401, PluginRequestFailureKind.Authentication)]
    [InlineData(403, PluginRequestFailureKind.Permission)]
    [InlineData(429, PluginRequestFailureKind.RateLimit)]
    [InlineData(503, PluginRequestFailureKind.ServerError)]
    public async Task CredentialChecksClassifyErrorsWithoutLeakingBodies(int status, PluginRequestFailureKind kind)
    {
        using var plugin = new DeepgramPlugin(new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-provider-response") });
        })));
        await plugin.ActivateAsync(Host);
        await plugin.SetApiKeyAsync("test-key");
        var error = await Assert.ThrowsAsync<PluginRequestException>(() => plugin.ValidateConfigurationAsync(default));
        Assert.Equal(kind, error.FailureKind);
        Assert.DoesNotContain("private-provider-response", error.ToString());
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var plugin = new DeepgramPlugin(new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new(HttpStatusCode.OK);
        })));
        await plugin.ActivateAsync(Host);
        await plugin.SetApiKeyAsync("test-key");
        using var cancel = new CancellationTokenSource();
        var request = plugin.ValidateConfigurationAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task PackageLoadsThroughPortableContractWithoutWpf()
    {
        var directory = Path.Combine(_root, "package");
        Directory.CreateDirectory(directory);
        File.Copy(typeof(DeepgramPlugin).Assembly.Location, Path.Combine(directory, "TypeWhisper.Plugin.Deepgram.dll"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "manifest.json"), Path.Combine(directory, "manifest.json"));
        await using var package = await PortablePluginPackage.LoadAsync(directory, Host, new(1, 1, 0));
        Assert.IsAssignableFrom<IApiKeyPlugin>(package.Plugin);
        var engine = Assert.IsAssignableFrom<ITranscriptionEnginePlugin>(package.Plugin);
        Assert.Equal("nova-3", engine.SelectedModelId);
        Assert.False(engine.SupportsLocalLivePreview);
        Assert.DoesNotContain(package.Plugin.GetType().Assembly.GetReferencedAssemblies(), reference => reference.Name == "PresentationFramework");
    }

    [Fact]
    public async Task RealPackageInstallsConfiguresRestartsAndReinstallsThroughGenericRegistry()
    {
        const string id = "com.typewhisper.deepgram";
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var file in new[] { typeof(DeepgramPlugin).Assembly.Location, Path.Combine(AppContext.BaseDirectory, "manifest.json") })
            {
                using var output = zip.CreateEntry(Path.GetFileName(file)).Open();
                using var input = File.OpenRead(file); input.CopyTo(output);
            }
        }
        var payload = bytes.ToArray();
        using var http = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(payload), RequestMessage = request })));
        var entry = new PortableCatalogEntry
        {
            Id = id, Name = "Deepgram", Version = "1.1.2", MinHostVersion = "1.1.0",
            DownloadUrl = "https://packages.test/deepgram.zip", Size = payload.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)),
            SupportedArchitectures = [PortablePluginCatalog.Architecture]
        };
        PortablePluginStore Store() => new(Path.Combine(_root, "store"), new(1, 1, 0), http, _ => Host);
        var store = Store(); await store.InitializeAsync();
        await store.InstallAsync(entry);
        await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 0), _ => Host))
        {
            await registry.InitializeAsync();
            Assert.Empty(registry.TranscriptionProviders);
            Assert.Null(await registry.SetEnabledAsync(id, true));
            Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
            await registry.UseConfigurationAsync(id, async (plugin, _) =>
            { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("saved-key"); return true; });
            await registry.RefreshCapabilitiesAsync();
            var provider = Assert.Single(registry.TranscriptionProviders);
            Assert.True(provider.Ready);
            Assert.True(provider.SupportsStreaming);
            Assert.True(Assert.Single(registry.Snapshot()).ApiKeyConfigured);
            await registry.SelectModelAsync(provider.ModelStates.Single(model => model.ModelId == "nova-2"));
        }
        var restarted = Store(); await restarted.InitializeAsync();
        await using (var registry = new PortablePluginRuntimeRegistry(restarted, new(1, 1, 0), _ => Host))
        {
            await registry.InitializeAsync();
            Assert.Equal("nova-2", Assert.Single(registry.TranscriptionProviders).SelectedModelId);
            Assert.Null(await registry.SetEnabledAsync(id, false));
            Assert.Empty(registry.TranscriptionProviders);
            await restarted.UninstallAsync(id);
        }
        Assert.Equal("saved-key", _secrets.Value);
        var reinstall = Store(); await reinstall.InitializeAsync();
        await reinstall.InstallAsync(entry);
        await using var finalRegistry = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 0), _ => Host);
        await finalRegistry.InitializeAsync();
        Assert.Empty(finalRegistry.TranscriptionProviders);
        Assert.Null(await finalRegistry.SetEnabledAsync(id, true));
        var restored = Assert.Single(finalRegistry.TranscriptionProviders);
        Assert.True(restored.Ready);
        Assert.Equal("nova-2", restored.SelectedModelId);
    }

    public void Dispose()
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private sealed class Secrets : IPluginSecretStore
    {
        public string? Value;
        public bool FailWrites;
        public Task StoreAsync(string key, string value) { if (FailWrites) throw new IOException(); Value = value; return Task.CompletedTask; }
        public Task<string?> LoadAsync(string key) => Task.FromResult(Value);
        public Task DeleteAsync(string key) { if (FailWrites) throw new IOException(); Value = null; return Task.CompletedTask; }
    }
}
