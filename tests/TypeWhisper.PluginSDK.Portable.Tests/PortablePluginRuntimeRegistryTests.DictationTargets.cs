using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

public sealed partial class PortablePluginRuntimeRegistryTests
{
    private const string TargetId = "test.typewhisper.target";

    // With a locality, the manifest declares it and its categories, as a catalog package does.
    private async Task<PortablePluginStore> TargetStore(string? locality = null, string[]? categories = null)
    {
        var bundles = Path.Join(_root, "bundles");
        Package(bundles, TargetId, typeof(DictationTargetProbePlugin));
        if (locality is not null)
            File.WriteAllText(Path.Join(bundles, TargetId, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
            {
                Id = TargetId, Name = "Runtime fixture", Version = "1.0.0", Locality = locality, Categories = categories,
                AssemblyName = "fixture.dll", PluginClass = typeof(DictationTargetProbePlugin).FullName!
            }));
        var store = new PortablePluginStore(Path.Join(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }

    [Fact]
    public async Task DictationTargets_ListEnabledTargetsWithTheirApps()
    {
        await using var registry = Registry(await TargetStore());
        Assert.Empty(registry.DictationTargets);

        Assert.Null(await registry.SetEnabledAsync(TargetId, true));

        var target = Assert.Single(registry.DictationTargets);
        Assert.Equal(TargetId, target.PluginId);
        Assert.Equal(["ProbeGame"], target.ProcessNames);
        Assert.True(target.Handles("probegame"));
        Assert.False(target.Handles("notepad"));
        Assert.Null(await registry.SetEnabledAsync(TargetId, false));
        Assert.Empty(registry.DictationTargets);
    }

    [Fact]
    public async Task DictationTargetCalls_RunWhileTheSharedQueueIsHeld()
    {
        await using var registry = Registry(await TargetStore());
        Assert.Null(await registry.SetEnabledAsync(TargetId, true));
        var target = Assert.Single(registry.DictationTargets);
        // Live transcription holds the shared queue for the whole recording.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = registry.UseConfigurationAsync(TargetId, async (_, _) => { await release.Task; return true; });
        while (!registry.IsBusy) await Task.Delay(10);

        var plan = await registry.UseDictationTargetAsync(target,
            (plugin, ct) => plugin.PrepareDictationAsync(new(Guid.NewGuid(), "ProbeGame", null), ct)).WaitAsync(TimeSpan.FromSeconds(5));
        var input = new RecordingInput();
        Assert.True(registry.TryUseDictationTarget(target, plugin => plugin.OnDictationPhaseChanged(Guid.NewGuid(), DictationTargetPhase.Recording, input), out var shown));

        Assert.Equal("Probe Game", plan!.AppName);
        Assert.Equal(DateTimeOffset.MaxValue, shown);
        Assert.Equal(["signal:70"], input.Calls);
        release.SetResult();
        Assert.True(await holding);
    }

    [Fact]
    public async Task Disable_CancelsARunningDelivery_AndReleasesThePackageAfterItReturned()
    {
        await using var registry = Registry(await TargetStore());
        Assert.Null(await registry.SetEnabledAsync(TargetId, true));
        var target = Assert.Single(registry.DictationTargets);
        var input = new RecordingInput();
        var delivery = registry.UseDictationTargetAsync(target, (plugin, ct) => plugin.DeliverDictationAsync(Guid.NewGuid(), "gg", input, ct));
        await Host(TargetId).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await registry.SetEnabledAsync(TargetId, false).WaitAsync(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        // The delivery's last key reports how often the package had been disposed when its code returned.
        Assert.Equal(["press:00"], input.Calls);
        Assert.Equal(1, Host(TargetId).GetSetting<int>("disposals"));
    }

    [Fact]
    public async Task ATargetFromAnEarlierActivation_IsRefused()
    {
        await using var registry = Registry(await TargetStore());
        Assert.Null(await registry.SetEnabledAsync(TargetId, true));
        var stale = Assert.Single(registry.DictationTargets);
        Assert.Null(await registry.SetEnabledAsync(TargetId, false));
        Assert.Null(await registry.SetEnabledAsync(TargetId, true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.UseDictationTargetAsync(stale, (plugin, ct) => plugin.PrepareDictationAsync(new(Guid.NewGuid(), "ProbeGame", null), ct)));
        Assert.False(registry.TryUseDictationTarget(stale, plugin => plugin.PluginId, out _));
        Assert.True(registry.TryUseDictationTarget(Assert.Single(registry.DictationTargets), plugin => plugin.PluginId, out var current));
        Assert.Equal(TargetId, current);
    }

    [Fact]
    public async Task APackageThePolicyRefuses_GetsNoDictation_BeforeThePolicyIsReapplied()
    {
        // A cloud package that declares only transcription: turning cloud transcription off refuses the whole package,
        // while the text rule alone would still hand it the dictation's text.
        var policy = Policy();
        await using var registry = PolicyRegistry(await TargetStore("cloud", ["transcription"]), () => policy);
        Assert.Null(await registry.SetEnabledAsync(TargetId, true));
        var target = Assert.Single(registry.DictationTargets);

        // No policy pass has unloaded the package yet, so every call judges the package itself.
        policy = Policy(cloudTranscription: false);

        Assert.Empty(registry.DictationTargets);
        var denied = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() =>
            registry.UseDictationTargetAsync(target, (plugin, ct) => plugin.PrepareDictationAsync(new(Guid.NewGuid(), "ProbeGame", null), ct)));
        Assert.Equal("plugin", denied.PolicyKey);
        Assert.Contains("cloud", denied.Message);
        Assert.False(registry.TryUseDictationTarget(target, plugin => plugin.PluginId, out _));
    }

    private sealed class RecordingInput : IDictationTargetInput
    {
        private readonly object _sync = new();
        private readonly List<string> _calls = [];
        public IReadOnlyList<string> Calls { get { lock (_sync) return _calls.ToArray(); } }
        public bool IsReady => true;
        public bool PressKeys(IReadOnlyList<ushort> virtualKeys) => Record("press:" + Keys(virtualKeys));
        public bool SignalKeys(IReadOnlyList<ushort> virtualKeys) => Record("signal:" + Keys(virtualKeys));
        public Task<bool> PasteAsync(string text, CancellationToken cancellationToken) => Task.FromResult(Record("paste:" + text));
        public Task WaitUntilResponsiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        private bool Record(string call) { lock (_sync) _calls.Add(call); return true; }
        private static string Keys(IReadOnlyList<ushort> keys) => string.Join("+", keys.Select(key => key.ToString("X2")));
    }
}
