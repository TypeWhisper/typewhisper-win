using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests : IDisposable
{
    private const string Id = "test.typewhisper.runtime";
    private const string OtherId = "test.typewhisper.runtime-other";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, ProbeHost> _hosts = new();
    private static readonly Version Version = new(1, 1, 0);

    private ProbeHost Host(string id)
    {
        if (!_hosts.TryGetValue(id, out var host)) _hosts.Add(id, host = new(Path.Combine(_root, "data", id)));
        return host;
    }
    private async Task<PortablePluginStore> Store(bool second = false)
    {
        var bundles = Path.Combine(_root, "bundles");
        Package(bundles, Id, typeof(RuntimeProbePlugin));
        if (second) Package(bundles, OtherId, typeof(OtherRuntimeProbePlugin));
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }
    private static void Package(string root, string id, Type type, string version = "1.0.0")
    {
        var folder = Path.Combine(root, id); Directory.CreateDirectory(folder);
        File.Copy(type.Assembly.Location, Path.Combine(folder, "fixture.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Name = "Runtime fixture", Version = version, AssemblyName = "fixture.dll", PluginClass = type.FullName!
        }));
    }
    private PortablePluginRuntimeRegistry Registry(PortablePluginStore store) => new(store, Version, id => Host(id));

    [Fact]
    public async Task DisableStillDisposesItsPackageWhenAnotherPackageHasDynamicCollisions()
    {
        var store = await Store(second: true);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Null(await registry.SetEnabledAsync(OtherId, true));
        await registry.UseConfigurationAsync(OtherId, async (plugin, _) => { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("collision"); return true; });
        await registry.RefreshCapabilitiesAsync();
        Assert.NotNull(await registry.SetEnabledAsync(Id, false));
        Assert.False(Host(Id).GetSetting<bool>("Enabled"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
        Assert.False(registry.Snapshot().Single(state => state.PluginId == Id).Enabled);
    }

    [Fact]
    public async Task ReservedPackagesAreNeverActivatedByTheRegistry()
    {
        var store = await Store();
        Host(Id).SetSetting("Enabled", true);
        await using var registry = new PortablePluginRuntimeRegistry(store, Version, id => Host(id), id => id != Id);
        await registry.InitializeAsync();
        Assert.Empty(registry.Snapshot());
        Assert.NotNull(await registry.SetEnabledAsync(Id, true));
        Assert.Equal(0, Host(Id).GetSetting<int>("activations"));
    }

    [Fact]
    public async Task ExistingPortableGroqPackageExposesBothCapabilitiesWithoutNetworkOrCredentials()
    {
        const string groqId = "com.typewhisper.groq";
        var bundles = Path.Combine(_root, "bundles");
        Package(bundles, groqId, typeof(TypeWhisper.Plugin.Groq.GroqPlugin), "1.0.6");
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(groqId, true));
        Assert.Equal(groqId, Assert.Single(registry.TranscriptionProviders).SelectionId);
        Assert.Equal(groqId, Assert.Single(registry.LlmProviders).SelectionId);
        Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
        Assert.False(Assert.Single(registry.LlmProviders).Ready);
        Assert.True(await registry.UseConfigurationAsync(groqId, (plugin, _) => Task.FromResult(plugin is IApiKeyPlugin)));
    }

    [Fact]
    public async Task MissingEnablementDoesNotActivateAndRealStorePathLoadsBothRolesOnce()
    {
        var store = await Store();
        await using var registry = Registry(store);
        await registry.InitializeAsync();
        Assert.False(Assert.Single(registry.Snapshot()).Enabled);
        Assert.Equal(0, Host(Id).GetSetting<int>("activations"));
        Assert.NotEqual(store.Resolve(Id), Assert.Single(store.Inventory()).Directory);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Equal(Id, Assert.Single(registry.TranscriptionProviders).SelectionId);
        Assert.Equal(Id, Assert.Single(registry.LlmProviders).SelectionId);
        Assert.Equal("hello", await registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", "hello", "llm", ct)));
        Assert.Equal("transcribed", (await registry.UseTranscriptionAsync(Id, (plugin, ct) => plugin.TranscribeAsync([], "de", false, null, ct))).Text);
        Assert.Equal(1, Host(Id).GetSetting<int>("activations"));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Equal(1, Host(Id).GetSetting<int>("deactivations"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
        Assert.False(Host(Id).GetSetting<bool>("Enabled"));
        Assert.Empty(registry.LlmProviders);
    }

    [Fact]
    public async Task AdditionalRolesRefreshReentrantlyAndBelongToTheSingleRootLifetime()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await registry.UseConfigurationAsync(Id, async (plugin, _) => { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("extra"); return true; });
        await registry.RefreshCapabilitiesAsync();
        Assert.Equal(2, registry.TranscriptionProviders.Count);
        Assert.Equal(2, registry.LlmProviders.Count);
        Assert.Equal("extra", await registry.UseLlmAsync(Id + "/extra", (plugin, ct) => plugin.ProcessAsync("", "extra", "llm", ct)));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Equal(1, Host(Id).GetSetting<int>("activations"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task FailedEnablePersistenceDisposesCandidateAndDoesNotPublishProviders()
    {
        var store = await Store();
        Host(Id).FailEnabledWrites = true;
        await using var registry = Registry(store);
        Assert.NotNull(await registry.SetEnabledAsync(Id, true));
        Assert.Empty(registry.TranscriptionProviders);
        Assert.False(Assert.Single(registry.Snapshot()).Enabled);
        Assert.NotEqual(true, Host(Id).GetSetting<bool?>("Enabled"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task FailedDisablePersistenceKeepsThePreviouslyEnabledOwnerUsable()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).FailEnabledWrites = true;
        Assert.NotNull(await registry.SetEnabledAsync(Id, false));
        Assert.True(Assert.Single(registry.Snapshot()).Enabled);
        Assert.True(Host(Id).GetSetting<bool>("Enabled"));
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Assert.Equal("still active", await registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", "still active", "llm", ct)));
    }

    [Fact]
    public async Task CrossPackageCollisionIsVisibleAndDoesNotReplaceTheExistingOwner()
    {
        var store = await Store(second: true);
        Host(Id).SetSetting("SharedSelection", "shared");
        Host(OtherId).SetSetting("SharedSelection", "shared");
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Contains("collision", await registry.SetEnabledAsync(OtherId, true));
        Assert.Equal(Id, Assert.Single(registry.TranscriptionProviders).PluginId);
        Assert.NotEqual(true, Host(OtherId).GetSetting<bool?>("Enabled"));
        Assert.Equal(1, Host(OtherId).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task DynamicCollisionRemovesAmbiguousCapabilitiesAndConfigurationCanRecover()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await registry.UseConfigurationAsync(Id, async (plugin, _) => { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("collision"); return true; });
        await registry.RefreshCapabilitiesAsync();
        Assert.Empty(registry.TranscriptionProviders);
        Assert.Contains("collision", Assert.Single(registry.Snapshot()).Error);
        await registry.UseConfigurationAsync(Id, async (plugin, _) => { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("extra"); return true; });
        await registry.RefreshCapabilitiesAsync();
        Assert.Equal(2, registry.TranscriptionProviders.Count);
        Assert.Null(Assert.Single(registry.Snapshot()).Error);
    }

    [Fact]
    public async Task DisableCancelsThenDrainsRequestsBeforeDisposalAndRejectsLateResults()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = registry.UseLlmAsync(Id, async (plugin, ct) =>
        {
            using var registration = ct.Register(() => { canceled.TrySetResult(true); throw new InvalidOperationException("Fixture cancellation callback failure."); });
            return await plugin.ProcessAsync("", "late result", "llm", ct);
        });
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = registry.SetEnabledAsync(Id, false);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(disable.IsCompleted);
        Assert.Empty(registry.LlmProviders);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(await disable);
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task ConfigurationWaitsForRequestsAndSavedEnablementRestoresOnRestart()
    {
        var store = await Store();
        await using (var registry = Registry(store))
        {
            Assert.Null(await registry.SetEnabledAsync(Id, true));
            Host(Id).SetSetting("Hold", true);
            var request = registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", "result", "llm", ct));
            await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var configuration = registry.UseConfigurationAsync(Id, async (plugin, _) => { await ((IApiKeyPlugin)plugin).SetApiKeyAsync("extra"); return true; });
            Assert.False(configuration.IsCompleted);
            Host(Id).Release.TrySetResult(null);
            Assert.Equal("result", await request);
            Assert.True(await configuration);
        }
        await using var restarted = Registry(store);
        await restarted.InitializeAsync();
        Assert.Equal(2, restarted.LlmProviders.Count);
        Assert.Equal(2, Host(Id).GetSetting<int>("activations"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task TextProcessorSnapshotNeverResolvesToAReenabledPackage()
    {
        var store = await Store();
        await using var registry = Registry(store);
        await registry.InitializeAsync();
        Assert.Empty(registry.PostProcessors);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var captured = Assert.Single(registry.PostProcessors);
        Assert.Equal("1.0.0", captured.Version);
        Assert.Equal("original", await registry.ProcessTextAsync(captured, "original", new()));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.NotEqual(captured.Generation, Assert.Single(registry.PostProcessors).Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.ProcessTextAsync(captured, "original", new()));
        Assert.Equal("new", await registry.ProcessTextAsync(Assert.Single(registry.PostProcessors), "new", new()));
    }

    [Fact]
    public async Task DisablingTextProcessorDrainsNativeWorkBeforeDisposalAndRejectsLateResult()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        var request = registry.ProcessTextAsync(Assert.Single(registry.PostProcessors), "late", new());
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = registry.SetEnabledAsync(Id, false);
        Assert.False(disable.IsCompleted);
        Assert.Empty(registry.PostProcessors);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Host(Id).Release.TrySetResult(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Null(await disable);
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task ActionSuccessSurvivesDisableAfterItsCommitAndOwnerStillDrains()
    {
        var store = await Store(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        var request = registry.ExecuteActionAsync(Assert.Single(registry.Actions), "input", new(null, null, null, null, null));
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disable = registry.SetEnabledAsync(Id, false);
        Assert.False(disable.IsCompleted);
        Assert.Empty(registry.Actions);
        Assert.Equal(0, Host(Id).GetSetting<int>("disposals"));
        Host(Id).Release.TrySetResult(null);
        Assert.Equal(PortableActionStatus.Succeeded, (await request).Status);
        Assert.Null(await disable);
        Assert.Equal(1, Host(Id).GetSetting<int>("actionWrites"));
        Assert.Equal(1, Host(Id).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task ActionExceptionIsUncertainAndNeverAutomaticallyRetried()
    {
        var store = await Store(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("ActionThrows", true);
        var result = await registry.ExecuteActionAsync(Assert.Single(registry.Actions), "input", new(null, null, null, null, null));
        Assert.Equal(PortableActionStatus.CompletionUnknown, result.Status);
        Assert.DoesNotContain("private action failure", result.Message);
        Assert.Equal(1, Host(Id).GetSetting<int>("actionWrites"));
    }

    [Fact]
    public async Task ActionCanceledBeforeAdmissionDoesNotInvokeAndReenabledSnapshotIsRejected()
    {
        var store = await Store(); await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var selected = Assert.Single(registry.Actions);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExecuteActionAsync(selected, "input", new(null, null, null, null, null), canceled.Token));
        Assert.Equal(0, Host(Id).GetSetting<int>("actionWrites"));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await registry.ExecuteActionAsync(selected, "input", new(null, null, null, null, null)));
        Assert.Equal(0, Host(Id).GetSetting<int>("actionWrites"));
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class ProbeHost(string directory) : IPluginHostServices
    {
        private readonly VocabularyHostServices _inner = new(directory);
        internal bool FailEnabledWrites;
        internal Action? OnCapabilitiesChanged;
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StoreSecretAsync(string key, string value) => throw new NotSupportedException();
        public Task<string?> LoadSecretAsync(string key) => key == "hold" ? Release.Task : Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => throw new NotSupportedException();
        public T? GetSetting<T>(string key) => _inner.GetSetting<T>(key);
        public void SetSetting<T>(string key, T value)
        {
            if (key == "Enabled" && FailEnabledWrites) throw new IOException("Fixture write failure.");
            _inner.SetSetting(key, value);
        }
        public string PluginDataDirectory => _inner.PluginDataDirectory;
        public bool AllowLegacyDataMigration => false;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus => throw new NotSupportedException();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { if (message == "request-start") Started.TrySetResult(true); }
        public void NotifyCapabilitiesChanged() => OnCapabilitiesChanged?.Invoke();
        public IPluginLocalization Localization => throw new NotSupportedException();
    }
}
