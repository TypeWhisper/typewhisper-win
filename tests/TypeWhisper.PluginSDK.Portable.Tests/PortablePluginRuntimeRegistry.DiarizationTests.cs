using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

// Speaker diarization providers: indexed like the other roles, and gated like transcription because they receive audio.
public sealed partial class PortablePluginRuntimeRegistryTests
{
    private const string DiarizationId = "fixture-diarization";

    private async Task<PortablePluginStore> DiarizationStore(string locality)
    {
        var bundles = Path.Combine(_root, "diarization-bundles");
        var folder = Path.Combine(bundles, Id); Directory.CreateDirectory(folder);
        File.Copy(typeof(DiarizationProbePlugin).Assembly.Location, Path.Combine(folder, "fixture.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = Id, Name = "Diarization fixture", Version = "1.0.0", Locality = locality, Categories = ["diarization"],
            AssemblyName = "fixture.dll", PluginClass = typeof(DiarizationProbePlugin).FullName!
        }));
        var store = new PortablePluginStore(Path.Combine(_root, "diarization-store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }

    private static PluginDiarizationRequest Request(int? speakers = null) =>
        new(new float[32000]) { SpeakerCount = speakers, Language = "de" };

    [Fact]
    public async Task DiarizationProvidersAreListedWhileEnabledAndRunUnderTheLease()
    {
        var store = await DiarizationStore("local");
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        var provider = Assert.Single(registry.DiarizationProviders);
        Assert.Equal((Id, DiarizationId, "Diarization fixture"), (provider.PluginId, provider.ProviderId, provider.Name));
        Assert.True(provider.IsLocal);
        Assert.False(provider.ModelsInstalled);
        Assert.False(provider.Ready);
        Assert.Equal(new PluginSpeakerCountRange(2, 4), provider.SpeakerCounts);
        Assert.Equal("fixture-model", provider.Model?.Id);

        var result = await registry.DiarizeAsync(DiarizationId, Request(speakers: 3));
        Assert.Equal(["S1", "S2"], result.Turns.Select(turn => turn.SpeakerLabel));
        Assert.Equal(2, result.Turns[1].End);
        Assert.Equal(32000, Host(Id).GetSetting<int>("samples"));
        Assert.Equal(3, Host(Id).GetSetting<int?>("speakers"));
        Assert.Equal("de", Host(Id).GetSetting<string>("language"));

        Assert.Null(await registry.SetEnabledAsync(Id, false));
        Assert.Empty(registry.DiarizationProviders);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.DiarizeAsync(DiarizationId, Request()));
    }

    [Fact]
    public async Task SpeakerCountOutsideTheProviderRangeNeverReachesThePlugin()
    {
        var store = await DiarizationStore("local");
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.DiarizeAsync(DiarizationId, Request(speakers: 9)));
        Assert.Equal(0, Host(Id).GetSetting<int>("samples"));
    }

    [Fact]
    public async Task PreparedModelsArePublishedBeforePreparationReturnsAndCanBeRemoved()
    {
        var store = await DiarizationStore("local");
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var reports = new List<double>();

        // Read without a refresh of its own: a module diarizes right after preparing and checks readiness first.
        await registry.PrepareDiarizationModelsAsync(DiarizationId, new SynchronousProgress(reports.Add));
        Assert.True(Assert.Single(registry.DiarizationProviders).Ready);
        Assert.Contains(1d, reports);

        // Installed models are not downloaded again.
        await registry.PrepareDiarizationModelsAsync(DiarizationId);
        Assert.Equal(1, Host(Id).GetSetting<int>("prepares"));

        await registry.DeleteDiarizationModelsAsync(DiarizationId);
        await registry.RefreshCapabilitiesAsync();
        Assert.False(Assert.Single(registry.DiarizationProviders).ModelsInstalled);
    }

    [Theory]
    [InlineData("cloud")]
    [InlineData("configurable")]
    public async Task RemoteDiarizationFollowsTheCloudTranscriptionPolicy(string locality)
    {
        var store = await DiarizationStore(locality);
        var policy = Policy(cloudTranscription: false);
        await using var registry = PolicyRegistry(store, () => policy);
        await registry.InitializeAsync();

        // A package that only diarizes is refused as a whole and never loads.
        Assert.Contains("cloud", await registry.SetEnabledAsync(Id, true));
        Assert.Empty(registry.DiarizationProviders);
        Assert.Equal(0, Host(Id).GetSetting<int>("samples"));

        policy = Policy();
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Assert.Null(registry.DiarizationRefusal(DiarizationId));
        Assert.False(Assert.Single(registry.DiarizationProviders).IsLocal);

        // Once loaded, a policy that turns cloud audio off refuses the next call before any audio reaches the plugin.
        policy = Policy(cloudTranscription: false);
        Assert.NotNull(registry.DiarizationRefusal(DiarizationId));
        var refused = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.DiarizeAsync(DiarizationId, Request()));
        Assert.Equal("diarization", refused.PolicyKey);
        Assert.Equal(0, Host(Id).GetSetting<int>("samples"));
    }

    [Fact]
    public async Task LocalDiarizationRunsWhileCloudAudioIsTurnedOff()
    {
        var store = await DiarizationStore("local");
        await using var registry = PolicyRegistry(store, () => Policy(cloudTranscription: false, cloudText: false));
        await registry.InitializeAsync();
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        Assert.Null(registry.DiarizationRefusal(DiarizationId));
        Assert.Equal(2, (await registry.DiarizeAsync(DiarizationId, Request())).Turns.Count);
    }

    [Fact]
    public async Task PolicyPassCancelsARunningRemoteDiarizationAndReportsTheRefusal()
    {
        var store = await DiarizationStore("cloud");
        var policy = Policy();
        await using var registry = PolicyRegistry(store, () => policy);
        await registry.InitializeAsync();
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);

        var running = registry.DiarizeAsync(DiarizationId, Request());
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy = Policy(cloudTranscription: false);
        await registry.ReapplyPolicyAsync();

        await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(registry.DiarizationProviders);
    }

    [Fact]
    public async Task ModelDownloadPolicyRefusesAndCancelsDiarizationModelDownloads()
    {
        var store = await DiarizationStore("local");
        var policy = Policy(modelDownloads: false);
        await using var registry = PolicyRegistry(store, () => policy);
        await registry.InitializeAsync();
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        var refused = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.PrepareDiarizationModelsAsync(DiarizationId));
        Assert.Equal("model-download", refused.PolicyKey);
        Assert.Equal(0, Host(Id).GetSetting<int>("prepares"));

        policy = Policy();
        Host(Id).SetSetting("Hold", true);
        var download = registry.PrepareDiarizationModelsAsync(DiarizationId);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy = Policy(modelDownloads: false);
        await registry.ReapplyPolicyAsync();

        await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(Host(Id).GetSetting<bool>("Installed"));
    }

    // The plugin returns normally after the policy pass cancelled its request, so the lease's own cancellation check
    // fails the call: that is reported as the refusal as well.
    [Fact]
    public async Task PolicyCancellationAfterTheProviderReturnedIsReportedAsTheRefusal()
    {
        var store = await DiarizationStore("cloud");
        var policy = Policy();
        await using var registry = PolicyRegistry(store, () => policy);
        await registry.InitializeAsync();
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        Host(Id).SetSetting("IgnoreCancellation", true);

        var running = registry.DiarizeAsync(DiarizationId, Request());
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy = Policy(cloudTranscription: false);
        var reapplied = registry.ReapplyPolicyAsync();
        Host(Id).Release.TrySetResult(null);

        var refused = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("diarization", refused.PolicyKey);
        await reapplied.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ModelDownloadPolicyCancellationAfterTheDownloadReturnedIsReportedAsTheRefusal()
    {
        var store = await DiarizationStore("local");
        var policy = Policy();
        await using var registry = PolicyRegistry(store, () => policy);
        await registry.InitializeAsync();
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        Host(Id).SetSetting("IgnoreCancellation", true);

        var download = registry.PrepareDiarizationModelsAsync(DiarizationId);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        policy = Policy(modelDownloads: false);
        await registry.ReapplyPolicyAsync();
        Host(Id).Release.TrySetResult(null);

        var refused = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("model-download", refused.PolicyKey);
    }

    [Fact]
    public async Task CallerCancellationOfADiarizationStaysACancellation()
    {
        var store = await DiarizationStore("local");
        await using var registry = Registry(store);
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        Host(Id).SetSetting("Hold", true);
        using var cancellation = new CancellationTokenSource();

        var running = registry.DiarizeAsync(DiarizationId, Request(), cancellationToken: cancellation.Token);
        await Host(Id).Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
