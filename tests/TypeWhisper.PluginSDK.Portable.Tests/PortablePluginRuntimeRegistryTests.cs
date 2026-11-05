using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests : IDisposable
{
    private const string Id = "test.typewhisper.runtime";
    private const string OtherId = "test.typewhisper.runtime-other";
    private const string ThirdId = "test.typewhisper.runtime-third";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private readonly Dictionary<string, ProbeHost> _hosts = new();
    private static readonly Version Version = new(1, 1, 0);

    private ProbeHost Host(string id)
    {
        if (!_hosts.TryGetValue(id, out var host)) _hosts.Add(id, host = new(Path.Combine(_root, "data", id)));
        return host;
    }
    private async Task<PortablePluginStore> Store(bool second = false, bool third = false)
    {
        var bundles = Path.Combine(_root, "bundles");
        Package(bundles, Id, typeof(RuntimeProbePlugin));
        if (second) Package(bundles, OtherId, typeof(OtherRuntimeProbePlugin));
        if (third) Package(bundles, ThirdId, typeof(ThirdRuntimeProbePlugin));
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }
    private static void Package(string root, string id, Type type, string version = "1.0.0", bool isLocal = false)
    {
        var folder = Path.Combine(root, id); Directory.CreateDirectory(folder);
        File.Copy(type.Assembly.Location, Path.Combine(folder, "fixture.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = id, Name = "Runtime fixture", Version = version, IsLocal = isLocal, AssemblyName = "fixture.dll", PluginClass = type.FullName!
        }));
    }
    private PortablePluginRuntimeRegistry Registry(PortablePluginStore store) => new(store, Version, id => Host(id));

    [Fact]
    public async Task ReadOnlyConfigurationSnapshotDoesNotScheduleAnotherChangeNotification()
    {
        var store = await Store();
        await using var registry = Registry(store);
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationChanges = 0;
        void OnActivationChanged()
        {
            // Enabling publishes once directly and once through the probe's queued
            // activation notification. Wait for both before observing read-only work.
            if (Interlocked.Increment(ref activationChanges) == 2) activated.TrySetResult();
        }
        registry.Changed += OnActivationChanged;
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Changed -= OnActivationChanged;
        var changes = 0;
        registry.Changed += () => Interlocked.Increment(ref changes);
        Assert.Equal(Id, await registry.UseConfigurationAsync(Id,
            (plugin, _) => Task.FromResult(plugin.PluginId), refreshCapabilities: false));
        await Task.Delay(100);
        Assert.Equal(0, changes);
        await registry.RefreshCapabilitiesAsync();
        Assert.Equal(1, changes);
    }

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
    public async Task StartupReadsEachPackageOnceAndAnnouncesThePassOnceWithoutRewritingEnablement()
    {
        var store = await Store(second: true, third: true);
        string[] ids = [Id, OtherId, ThirdId];
        var saved = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in ids)
        {
            Host(id).SetSetting("Enabled", true);
            Host(id).SetSetting("QuietActivation", true);
            File.SetLastWriteTimeUtc(Path.Combine(Host(id).PluginDataDirectory, "settings.json"), saved);
        }
        await using var registry = Registry(store);
        var changes = 0;
        registry.Changed += () => Interlocked.Increment(ref changes);
        await registry.InitializeAsync();
        Assert.Equal(ids.Order(), registry.TranscriptionProviders.Select(provider => provider.PluginId).Order());
        Assert.Equal(1, changes);
        // Enabled was already saved as true, so no settings file was written.
        Assert.All(ids, id => Assert.Equal(saved, File.GetLastWriteTimeUtc(Path.Combine(Host(id).PluginDataDirectory, "settings.json"))));
        // A refresh reads every package once. Startup read each package as often, not once per later package as well.
        var startupReads = ids.Select(id => Host(id).Reads("SharedSelection")).ToArray();
        await registry.RefreshCapabilitiesAsync();
        Assert.All(startupReads, reads => Assert.True(reads > 0));
        Assert.Equal(startupReads, ids.Select((id, index) => Host(id).Reads("SharedSelection") - startupReads[index]));
    }

    [Fact]
    public async Task ActivationNotificationsDuringStartupAreAnsweredByOneRefreshAfterThePass()
    {
        var store = await Store(second: true, third: true);
        string[] ids = [Id, OtherId, ThirdId];
        // Without QuietActivation each probe reports a capability change while it activates.
        foreach (var id in ids) Host(id).SetSetting("Enabled", true);
        await using var registry = Registry(store);
        var changes = 0;
        registry.Changed += () => Interlocked.Increment(ref changes);
        await registry.InitializeAsync();
        await Task.Delay(200);
        Assert.Equal(1, changes);
        Assert.Equal(ids.Order(), registry.TranscriptionProviders.Select(provider => provider.PluginId).Order());
        // Each package was read for its activation and once more after the pass, never between activations.
        var afterStartup = ids.Select(id => Host(id).Reads("SharedSelection")).ToArray();
        await registry.RefreshCapabilitiesAsync();
        var perRefresh = ids.Select((id, index) => Host(id).Reads("SharedSelection") - afterStartup[index]).ToArray();
        Assert.All(perRefresh, reads => Assert.True(reads > 0));
        Assert.Equal(perRefresh.Select(reads => 2 * reads), afterStartup);
    }

    [Fact]
    public async Task StartupKeepsTheDictationTargetOfAPackageActivatedEarlierInThePass()
    {
        var bundles = Path.Join(_root, "bundles");
        Package(bundles, TargetId, typeof(DictationTargetProbePlugin));
        Package(bundles, Id, typeof(RuntimeProbePlugin));
        var store = new PortablePluginStore(Path.Join(_root, "store"), Version, _http);
        // The target activates first, so the later activation keeps the target's entries from the published index.
        await store.InitializeAsync(bundles, bootstrapPluginIds: [TargetId, Id]);
        Host(TargetId).SetSetting("Enabled", true);
        Host(Id).SetSetting("Enabled", true);
        Host(Id).SetSetting("QuietActivation", true);
        await using var registry = Registry(store);
        await registry.InitializeAsync();
        Assert.Equal(TargetId, Assert.Single(registry.DictationTargets).PluginId);
        Assert.Equal(Id, Assert.Single(registry.TranscriptionProviders).PluginId);
    }

    [Fact]
    public async Task StartupCollisionKeepsTheEarlierOwnerAndReleasesTheLaterPackage()
    {
        var store = await Store(second: true);
        foreach (var id in new[] { Id, OtherId }) { Host(id).SetSetting("Enabled", true); Host(id).SetSetting("SharedSelection", "shared"); }
        await using var registry = Registry(store);
        await registry.InitializeAsync();
        Assert.Equal(Id, Assert.Single(registry.TranscriptionProviders).PluginId);
        var other = registry.Snapshot().Single(state => state.PluginId == OtherId);
        Assert.False(other.Enabled);
        Assert.Contains("collision", other.Error);
        Assert.True(Host(OtherId).GetSetting<bool>("Enabled"));
        Assert.Equal(1, Host(OtherId).GetSetting<int>("disposals"));
        Assert.Equal(Id, await registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", Id, "llm", ct)));
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
        Package(bundles, groqId, typeof(TypeWhisper.Plugin.Groq.GroqPlugin), "1.1.1");
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
    public async Task CancelingSettingsTransferReleasesQueuedForegroundLlmRequest()
    {
        var store = await Store();
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = registry.UseConfigurationAsync(Id, async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, cancellation.Token, preserveCompletedResult: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var foreground = registry.UseLlmAsync(Id, (plugin, ct) => plugin.ProcessAsync("", "foreground", "llm", ct));
        Assert.False(foreground.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal("foreground", await foreground.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await registry.UseConfigurationAsync(Id, (_, _) => Task.FromResult(true)));
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
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reads = new(StringComparer.Ordinal);
        internal bool FailEnabledWrites;
        internal Action? OnCapabilitiesChanged;
        internal int Reads(string key) => _reads.GetValueOrDefault(key);
        internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StoreSecretAsync(string key, string value) => throw new NotSupportedException();
        public Task<string?> LoadSecretAsync(string key) => key == "hold" ? Release.Task : Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => throw new NotSupportedException();
        public T? GetSetting<T>(string key)
        {
            _reads.AddOrUpdate(key, 1, (_, count) => count + 1);
            return _inner.GetSetting<T>(key);
        }
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
