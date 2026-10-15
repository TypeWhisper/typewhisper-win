using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;

// Machine policy at the registry and store boundaries: refused packages never load, refused content never leaves.
public sealed partial class PortablePluginRuntimeRegistryTests
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    private static ManagedProviderPolicy Policy(bool? cloudTranscription = null, bool? cloudText = null,
        string[]? transcriptionProviders = null, string[]? llmProviders = null, string[]? plugins = null,
        bool? pluginDownloads = null, bool? modelDownloads = null) => new(
        cloudTranscription, cloudText,
        transcriptionProviders is null ? null : new HashSet<string>(transcriptionProviders, StringComparer.OrdinalIgnoreCase),
        llmProviders is null ? null : new HashSet<string>(llmProviders, StringComparer.OrdinalIgnoreCase),
        plugins is null ? null : new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase),
        pluginDownloads, modelDownloads, NoKeys);

    // Like Store(), but with the fixture package declaring a locality through the manifest.
    private async Task<PortablePluginStore> LocalityStore(string locality)
    {
        var bundles = Path.Combine(_root, "bundles");
        var folder = Path.Combine(bundles, Id); Directory.CreateDirectory(folder);
        File.Copy(typeof(RuntimeProbePlugin).Assembly.Location, Path.Combine(folder, "fixture.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        {
            Id = Id, Name = "Runtime fixture", Version = "1.0.0", Locality = locality, AssemblyName = "fixture.dll", PluginClass = typeof(RuntimeProbePlugin).FullName!
        }));
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http);
        await store.InitializeAsync(bundles);
        return store;
    }

    private PortablePluginRuntimeRegistry PolicyRegistry(PortablePluginStore store, Func<ManagedProviderPolicy> policy) =>
        new(store, Version, id => Host(id), null, policy);

    [Fact]
    public async Task PackageOutsideTheAllowlistIsNeverLoaded()
    {
        var store = await Store();
        Host(Id).SetSetting("Enabled", true);
        await using var registry = PolicyRegistry(store, () => Policy(plugins: ["com.typewhisper.sherpa-onnx"]));

        await registry.InitializeAsync();

        var state = Assert.Single(registry.Snapshot());
        Assert.False(state.Enabled);
        Assert.Contains("not allowed", state.Error);
        Assert.Equal(0, Host(Id).GetSetting<int>("activations"));
        Assert.Contains("not allowed", await registry.SetEnabledAsync(Id, true));
        Assert.Empty(registry.TranscriptionProviders);
        Assert.Equal(0, Host(Id).GetSetting<int>("activations"));
        Assert.Null(await registry.SetEnabledAsync(Id, false));
    }

    [Fact]
    public async Task CloudTranscriptionPolicyRefusesACloudPackage()
    {
        var store = await LocalityStore("cloud");
        await using var registry = PolicyRegistry(store, () => Policy(cloudTranscription: false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        var provider = Assert.Single(registry.TranscriptionProviders);
        Assert.Equal(ContentLocality.Cloud, provider.Locality);
        Assert.Contains("Cloud transcription is turned off", registry.TranscriptionRefusal(Id));
        var denied = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() =>
            registry.UseTranscriptionAsync(Id, (engine, _) => Task.FromResult(engine.PluginId)));
        Assert.Equal("transcription", denied.PolicyKey);
    }

    [Fact]
    public async Task CloudTranscriptionPolicyPassesALocalPackage()
    {
        var store = await LocalityStore("local");
        await using var registry = PolicyRegistry(store, () => Policy(cloudTranscription: false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        Assert.Equal(ContentLocality.Local, Assert.Single(registry.TranscriptionProviders).Locality);
        Assert.Null(registry.TranscriptionRefusal(Id));
        Assert.Equal(Id, await registry.UseTranscriptionAsync(Id, (engine, _) => Task.FromResult(engine.PluginId)));
    }

    [Fact]
    public async Task AllowedTranscriptionProvidersRefuseOtherSelectionIdsEvenWhenLocal()
    {
        var store = await LocalityStore("local");
        await using var registry = PolicyRegistry(store, () => Policy(transcriptionProviders: ["com.typewhisper.sherpa-onnx"]));
        Assert.Null(await registry.SetEnabledAsync(Id, true));

        var denied = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() =>
            registry.UseTranscriptionAsync(Id, (engine, _) => Task.FromResult(engine.PluginId)));

        Assert.Contains("not allowed", denied.Message);
        Assert.Contains("not allowed", registry.TranscriptionRefusal(Id));
        Assert.Null(registry.TranscriptionRefusal("unknown-role"));
    }

    [Fact]
    public async Task CloudTextPolicyRefusesLlmProcessorAndActionOfACloudPackage()
    {
        var store = await LocalityStore("configurable");
        await using var registry = PolicyRegistry(store, () => Policy(cloudText: false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var processor = Assert.Single(registry.PostProcessors);
        var action = Assert.Single(registry.Actions);

        await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.UseLlmAsync(Id, (provider, _) => Task.FromResult(provider.PluginId)));
        await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.ProcessTextAsync(processor, "text", new PostProcessingContext()));
        await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.ExecuteActionAsync(action, "text", new ActionContext(null, null, null, null, null)));
        Assert.Contains("turned off", registry.LlmRefusal(Id));
        Assert.Contains("turned off", registry.TextTransferRefusal(Id));
        // Audio is governed by the other key.
        Assert.Null(registry.TranscriptionRefusal(Id));
    }

    [Fact]
    public async Task StoreRefusesInstallsBeforeAnyDownloadWhenPolicySaysSo()
    {
        var requests = 0;
        using var http = new HttpClient(new CountingHandler(() => requests++));
        var policy = Policy(pluginDownloads: false);
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, http, policy: () => policy);
        await store.InitializeAsync();
        var entry = new PortableCatalogEntry
        {
            Id = "com.typewhisper.example", Name = "Example", Version = "1.0.0", DownloadUrl = "https://packages.test/example.zip",
            Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])), Size = 3, SupportedArchitectures = [PortablePluginCatalog.Architecture]
        };

        var denied = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => store.InstallAsync(entry));

        Assert.Equal("plugin-download", denied.PolicyKey);
        Assert.Contains("Plugin downloads are turned off", denied.Message);
        Assert.Equal(0, requests);
        Assert.Contains("Plugin downloads are turned off", store.InstallRefusal(entry.Id));

        policy = Policy(plugins: ["com.typewhisper.other"]);
        Assert.Contains("not allowed", store.InstallRefusal(entry.Id));
        policy = Policy();
        Assert.Null(store.InstallRefusal(entry.Id));
    }

    [Fact]
    public async Task UpdatesDoNotOfferPackagesPolicyWouldRefuse()
    {
        var policy = ManagedProviderPolicy.None;
        var bundles = Path.Combine(_root, "bundles");
        Package(bundles, Id, typeof(RuntimeProbePlugin));
        var store = new PortablePluginStore(Path.Combine(_root, "store"), Version, _http, policy: () => policy);
        await store.InitializeAsync(bundles);
        var updates = new PortablePluginUpdates(store, new PortablePluginCatalog(_http), Version, PortablePluginCatalog.Architecture);
        var entry = new PortableCatalogEntry
        {
            Id = Id, Name = "Runtime fixture", Version = "1.1.0", DownloadUrl = "https://packages.test/fixture.zip",
            Sha256 = new string('a', 64), Size = 10, SupportedArchitectures = [PortablePluginCatalog.Architecture]
        };
        updates.AcceptCatalog([entry]);

        Assert.True(updates.IsAvailable(entry));
        policy = Policy(pluginDownloads: false);
        Assert.False(updates.IsAvailable(entry));
        Assert.Empty(updates.Available);
    }

    [Fact]
    public async Task ModelDownloadPolicyRefusesRegistryDownloads()
    {
        var store = await Store();
        Host(Id).SetSetting("LocalModels", true);
        await using var registry = PolicyRegistry(store, () => Policy(modelDownloads: false));
        Assert.Null(await registry.SetEnabledAsync(Id, true));
        var model = Assert.Single(registry.TranscriptionProviders).ModelStates.First();

        var denied = await Assert.ThrowsAsync<ManagedPolicyDeniedException>(() => registry.DownloadModelAsync(model));

        Assert.Equal("model-download", denied.PolicyKey);
        Assert.Contains("Model downloads are turned off", denied.Message);
    }

    private sealed class CountingHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
