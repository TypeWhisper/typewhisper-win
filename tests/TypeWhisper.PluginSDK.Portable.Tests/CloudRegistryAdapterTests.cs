using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;
using TypeWhisper.WinUI;

public sealed class CloudRegistryAdapterTests : IDisposable
{
    private const string Id = "com.typewhisper.groq";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloud-registry-test-" + Guid.NewGuid().ToString("N"));
    private readonly ProbeHost _host;
    private readonly HttpClient _http;
    private byte[] _archive = [];
    private static readonly Version Version = new(1, 1, 0);
    public CloudRegistryAdapterTests()
    {
        _host = new(Path.Combine(_root, "data"));
        _http = new(new ArchiveHandler(() => _archive));
    }
    private string Bundle()
    {
        var root = Path.Combine(_root, "bundled");
        var directory = Path.Combine(root, Id); Directory.CreateDirectory(directory);
        File.Copy(typeof(CloudRegistryProbePlugin).Assembly.Location, Path.Combine(directory, "probe.dll"), true);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = Id, Name = "Registry adapter fixture", Version = "1.0.0", AssemblyName = "probe.dll",
            PluginClass = typeof(CloudRegistryProbePlugin).FullName!
        }));
        return root;
    }
    private async Task<PortablePluginStore> Store(bool bundled = true)
    {
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundled ? Bundle() : null);
        return store;
    }
    private PortablePluginRuntimeRegistry Registry(PortablePluginStore store) => new(store, Version, _ => _host);
    private PortableCatalogEntry ArchiveEntry()
    {
        var directory = Path.Combine(Bundle(), Id);
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in Directory.GetFiles(directory)) archive.CreateEntryFromFile(file, Path.GetFileName(file));
        _archive = stream.ToArray();
        return new PortableCatalogEntry { Id = Id, Name = "Registry adapter fixture", Version = "1.0.0",
            DownloadUrl = "https://fixture.invalid/package.zip", Size = _archive.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(_archive)), SupportedArchitectures = [PortablePluginCatalog.Architecture] };
    }

    [Fact]
    public async Task SaveKeyEnablesTheSingleRegistryOwnerAndUsesExistingPreferenceNamesAcrossRestart()
    {
        var store = await Store();
        await using (var registry = Registry(store))
        await using (var cloud = new CloudTranscriptionPlugin(_host, registry))
        {
            await registry.InitializeAsync(); await cloud.InitializeAsync();
            Assert.False(cloud.Enabled); Assert.False(cloud.Ready);
            await cloud.SaveKeyAsync("  fixture-key  ");
            Assert.True(cloud.Enabled); Assert.True(cloud.Ready);
            Assert.Equal(1, _host.GetSetting<int>("activations"));
            await cloud.SelectModelAsync("second"); cloud.SelectLanguage("de");
            await cloud.ValidateAsync();
            Assert.Equal(1, _host.GetSetting<int>("validations"));
            Assert.Equal("second", cloud.ModelId); Assert.True(cloud.SupportsTranslation);
            var decoded = await cloud.DecodeAsync([0, .25f], translate: true);
            Assert.Equal("fixture transcript", decoded.Text); Assert.Equal("de", decoded.DetectedLanguage);
            Assert.Equal("de", _host.GetSetting<string>("lastLanguage"));
            Assert.Equal("second", _host.GetSetting<string>("lastModel"));
            Assert.True(_host.GetSetting<bool>("lastTranslate"));
            Assert.Equal(48, _host.GetSetting<int>("wavLength"));
            Assert.True(_host.GetSetting<bool>("Enabled"));
            Assert.Equal("second", _host.GetSetting<string>("selectedModel"));
            Assert.Equal("de", _host.GetSetting<string>("Language"));
            Assert.DoesNotContain("fixture-key", File.ReadAllText(Path.Combine(_host.PluginDataDirectory, "settings.json")));
        }
        await using var restartedRegistry = Registry(store);
        await using var restartedCloud = new CloudTranscriptionPlugin(_host, restartedRegistry);
        await restartedRegistry.InitializeAsync(); await restartedCloud.InitializeAsync();
        Assert.True(restartedCloud.Ready); Assert.Equal("second", restartedCloud.ModelId); Assert.Equal("de", restartedCloud.Language);
        Assert.Equal(2, _host.GetSetting<int>("activations"));
    }

    [Fact]
    public async Task EnabledWithoutKeyIsNotReadyAndFailedEnableSaveDoesNotPublishFalseReadiness()
    {
        var store = await Store();
        await using var registry = Registry(store);
        await using var cloud = new CloudTranscriptionPlugin(_host, registry);
        _host.FailEnabledWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.SaveKeyAsync("key"));
        Assert.False(cloud.Enabled); Assert.False(cloud.Ready); Assert.Empty(_host.Secrets);
        Assert.NotEqual(true, _host.GetSetting<bool?>("Enabled"));
        _host.FailEnabledWrites = false;
        await cloud.SetEnabledAsync(true);
        Assert.True(cloud.Enabled); Assert.False(cloud.Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.DecodeAsync([.1f]));
        Assert.False(_host.DecodeStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task FailedSecretAndModelWritesPreserveTheWorkingConfiguration()
    {
        var store = await Store();
        await using var registry = Registry(store);
        await using var cloud = new CloudTranscriptionPlugin(_host, registry);
        await cloud.SaveKeyAsync("original");
        _host.FailSecretWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.SaveKeyAsync("replacement"));
        Assert.True(cloud.Ready); Assert.Equal("original", _host.Secrets["api-key"]);
        _host.FailModelWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.SelectModelAsync("second"));
        Assert.Equal("first", cloud.ModelId);
        _host.FailSecretWrites = false;
        await cloud.SaveKeyAsync("");
        Assert.False(cloud.Ready); Assert.Empty(_host.Secrets);
    }

    [Fact]
    public async Task RegistryDisableDrainsAdapterDecodeBeforeUninstallAndKeepsPreferencesAndSecrets()
    {
        var store = await Store();
        await using var registry = Registry(store);
        await using var cloud = new CloudTranscriptionPlugin(_host, registry);
        await cloud.SaveKeyAsync("keep-key"); await cloud.SelectModelAsync("second"); cloud.SelectLanguage("de");
        _host.SetSetting("HoldDecode", true);
        var decode = cloud.DecodeAsync([.1f]);
        await _host.DecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cloud.SetEnabledAsync(false)); // Existing UI concurrency guard.
        var disable = registry.SetEnabledAsync(Id, false);
        Assert.False(disable.IsCompleted); Assert.True(store.IsInstalled(Id));
        Assert.Equal(0, _host.GetSetting<int>("disposals"));
        _host.ReleaseDecode.TrySetResult(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => decode);
        Assert.Null(await disable);
        Assert.Equal(1, _host.GetSetting<int>("disposals"));
        await store.UninstallAsync(Id);
        Assert.False(store.IsInstalled(Id)); Assert.False(cloud.Enabled); Assert.False(cloud.Ready);
        Assert.Equal("keep-key", _host.Secrets["api-key"]);
        Assert.Equal("second", _host.GetSetting<string>("selectedModel")); Assert.Equal("de", _host.GetSetting<string>("Language"));
    }

    [Fact]
    public async Task DisposingTheCloudAdapterDoesNotDisposeTheSharedLlmPackageOwner()
    {
        var store = await Store();
        await using var registry = Registry(store);
        var cloud = new CloudTranscriptionPlugin(_host, registry);
        await cloud.SaveKeyAsync("key"); await cloud.DisposeAsync();
        Assert.False(cloud.Enabled); Assert.False(cloud.Ready);
        Assert.Equal(0, _host.GetSetting<int>("disposals"));
        Assert.Equal("LLM still available", await registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", "LLM still available", "llm", ct)));
        await registry.DisposeAsync(); Assert.Equal(1, _host.GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task NewlyInstalledPackageGetsDynamicManagementBindingWithoutRecreatingController()
    {
        var store = await Store(bundled: false);
        await using var registry = Registry(store);
        var management = new PluginManagementController(store.InventoryRoot,
            id => new ManagedPluginBinding(id, () => registry.Snapshot().Any(state => state.PluginId == id && state.Enabled),
                () => false, () => registry.Snapshot().FirstOrDefault(state => state.PluginId == id)?.Error,
                enabled => registry.SetEnabledAsync(id, enabled)), () => true, () => Task.FromResult(store.Inventory()));
        await management.RefreshAsync(); Assert.Empty(management.Snapshot());
        await store.InstallAsync(ArchiveEntry()); await management.RefreshAsync();
        var installed = Assert.Single(management.Snapshot());
        Assert.True(installed.Supported); Assert.False(installed.Enabled); Assert.Equal(0, _host.GetSetting<int>("activations"));
        Assert.Null(await management.SetEnabledAsync(installed.Package.Directory, true));
        Assert.True(Assert.Single(management.Snapshot()).Enabled); Assert.Equal(1, _host.GetSetting<int>("activations"));
        Assert.Null(await management.SetEnabledAsync(installed.Package.Directory, false));
        await store.UninstallAsync(Id); await management.RefreshAsync(); Assert.Empty(management.Snapshot());
    }

    [Fact]
    public async Task RegistryNotificationsAndCloudOperationsCompleteOnAPumpingUiContext()
    {
        var store = await Store();
        await RunUiAsync(async () =>
        {
            var context = SynchronizationContext.Current;
            await using var registry = Registry(store);
            await using var cloud = new CloudTranscriptionPlugin(_host, registry);
            cloud.Changed += () => { _ = cloud.Models.Count; _ = registry.Snapshot().Count; };
            await cloud.SaveKeyAsync("key"); await cloud.SelectModelAsync("second");
            await cloud.ValidateAsync(); Assert.Same(context, SynchronizationContext.Current);
            Assert.Equal("fixture transcript", (await cloud.DecodeAsync([.1f])).Text);
            Assert.Same(context, SynchronizationContext.Current);
            await cloud.SetEnabledAsync(false); Assert.False(cloud.Busy);
        }).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task RunUiAsync(Func<Task> action)
    {
        using var queue = new BlockingCollection<(SendOrPostCallback Callback, object? State)>();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(() =>
        {
            var context = new PumpContext(queue); SynchronizationContext.SetSynchronizationContext(context);
            Task operation;
            try { operation = action(); } catch (Exception ex) { completion.TrySetException(ex); return; }
            _ = operation.ContinueWith(finished =>
            {
                if (finished.IsFaulted) completion.TrySetException(finished.Exception!.InnerExceptions);
                else if (finished.IsCanceled) completion.TrySetCanceled();
                else completion.TrySetResult();
                queue.CompleteAdding();
            }, TaskScheduler.Default);
            foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
            SynchronizationContext.SetSynchronizationContext(null);
        });
        await Task.WhenAll(completion.Task, worker);
    }
    private sealed class PumpContext(BlockingCollection<(SendOrPostCallback Callback, object? State)> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    }
    private sealed class ArchiveHandler(Func<byte[]> archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive()), RequestMessage = request });
    }
    private sealed class ProbeHost(string directory) : IPluginHostServices
    {
        private readonly VocabularyHostServices _settings = new(directory);
        internal readonly Dictionary<string, string> Secrets = [];
        internal bool FailEnabledWrites, FailSecretWrites, FailModelWrites;
        internal TaskCompletionSource DecodeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> ReleaseDecode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StoreSecretAsync(string key, string value) { if (FailSecretWrites) throw new IOException(); Secrets[key] = value; return Task.CompletedTask; }
        public Task<string?> LoadSecretAsync(string key) => key == "pause" ? ReleaseDecode.Task : Task.FromResult(Secrets.GetValueOrDefault(key));
        public Task DeleteSecretAsync(string key) { Secrets.Remove(key); return Task.CompletedTask; }
        public T? GetSetting<T>(string key) => _settings.GetSetting<T>(key);
        public void SetSetting<T>(string key, T value)
        {
            if (key == "Enabled" && FailEnabledWrites || key == "selectedModel" && FailModelWrites) throw new IOException();
            _settings.SetSetting(key, value);
        }
        public string PluginDataDirectory => _settings.PluginDataDirectory;
        public bool AllowLegacyDataMigration => false;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => throw new NotSupportedException();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { if (message == "decode-start") DecodeStarted.TrySetResult(); }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization => throw new NotSupportedException();
    }
    public void Dispose()
    {
        _http.Dispose(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
