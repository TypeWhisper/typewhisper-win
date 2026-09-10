using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSDK.PortableFixture;
using Xunit;

public sealed class PortablePluginStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plugin-store-" + Guid.NewGuid().ToString("N"));
    private byte[] _download = [];
    private readonly HttpClient _http;
    private const string Id = "com.test.lifecycle";
    public PortablePluginStoreTests() { Directory.CreateDirectory(_root); _http = new(new Handler(() => _download)); }
    public void Dispose()
    {
        _http.Dispose();
        // Collectible load contexts release their mapped test DLLs after collection.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private PortablePluginStore Store(bool hooks = false) => new(Path.Combine(_root, "store"), new(1, 1, 0), _http,
        hooks ? _ => Host : null);
    private VocabularyHostServices Host => new(Path.Combine(_root, "data"));

    [Fact]
    public async Task EmptyHostInstallsOnDemandAndPreservesInstallationAcrossRestart()
    {
        var store = Store();
        await store.InitializeAsync();
        Assert.False(store.IsInstalled(Id));
        Assert.False(await store.InstallAsync(Entry()));
        var installed = store.Resolve(Id);
        var restarted = Store();
        await restarted.InitializeAsync();
        Assert.Equal(installed, restarted.Resolve(Id));
        Assert.Equal("1.0.0", restarted.InstalledVersion(Id));
    }

    [Fact]
    public async Task UpdateAllContinuesWithOtherPackagesAfterOneFailure()
    {
        const string otherId = "com.test.other";
        var store = Store(); await store.InitializeAsync();
        await store.InstallAsync(Entry());
        await store.InstallAsync(Entry(id: otherId));
        var first = Entry("1.1.0") with { Name = "First", Sha256 = new string('a', 64) };
        var second = Entry("1.1.0", id: otherId) with { Name = "Second" };
        var updates = new PortablePluginUpdates(store, new(_http), new(1, 1, 0), PortablePluginCatalog.Architecture);
        updates.AcceptCatalog([first, second]);
        Assert.Equal(2, updates.Available.Count);
        await updates.UpdateAsync();
        Assert.False(store.PendingRestart(Id));
        Assert.True(store.PendingRestart(otherId));
        Assert.Equal(Id, Assert.Single(updates.Available).Id);
        Assert.Contains("1 updated", updates.Status);
        Assert.Contains("First", updates.Status);
        await updates.ShutdownAsync();
    }

    [Fact]
    public async Task UpdatePlanOnlyIncludesNewerCompatibleInstalledPackagesAndSkipsPendingUpdates()
    {
        var store = Store(); await store.InitializeAsync();
        await store.InstallAsync(Entry());
        var offered = Entry("1.1.0");
        var updates = new PortablePluginUpdates(store, new(_http), new(1, 1, 0), PortablePluginCatalog.Architecture);
        updates.AcceptCatalog([offered with { SupportedArchitectures = ["unavailable"] },
            offered with { Id = "com.test.not-installed" }]);
        Assert.Empty(updates.Available);
        updates.AcceptCatalog([offered with { Version = "1.0.0" }]);
        Assert.Empty(updates.Available);
        updates.AcceptCatalog([offered]);
        Assert.True(updates.HasUpdate(Id));
        await updates.UpdateAsync();
        Assert.True(updates.RestartRequired);
        Assert.Empty(updates.Available);
        Assert.Equal("1.0.0", store.InstalledVersion(Id));
        await updates.UpdateAsync();
        Assert.Empty(updates.Available);
        await updates.ShutdownAsync();
        var restarted = Store(); await restarted.InitializeAsync();
        Assert.Equal("1.1.0", restarted.InstalledVersion(Id));
    }

    [Fact]
    public async Task FailedUpdateRemainsRetryableAndPreservesInstalledVersion()
    {
        var store = Store(); await store.InitializeAsync();
        await store.InstallAsync(Entry());
        var offered = Entry("1.1.0");
        var updates = new PortablePluginUpdates(store, new(_http), new(1, 1, 0), PortablePluginCatalog.Architecture);
        updates.AcceptCatalog([offered with { Sha256 = new string('a', 64) }]);
        await updates.UpdateAsync();
        Assert.Contains("Could not update", updates.Status);
        Assert.True(updates.HasUpdate(Id));
        Assert.False(updates.RestartRequired);
        Assert.Equal("1.0.0", store.InstalledVersion(Id));
        updates.AcceptCatalog([offered]);
        await updates.UpdateAsync(Id);
        Assert.True(updates.RestartRequired);
        await updates.ShutdownAsync();
    }
    private string Package(string version = "1.0.0", string id = Id)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        File.Copy(typeof(LifecycleProbePlugin).Assembly.Location, Path.Combine(folder, "plugin.dll"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        { Id = id, Name = "Fixture", Version = version, AssemblyName = "plugin.dll", PluginClass = typeof(LifecycleProbePlugin).FullName! }));
        return folder;
    }
    private PortableCatalogEntry Entry(string version = "1.0.0", string? unsafePath = null, string id = Id)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var file in Directory.GetFiles(Package(version, id))) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            if (unsafePath is not null) { using var output = new StreamWriter(zip.CreateEntry(unsafePath).Open()); output.Write("unsafe"); }
        }
        _download = stream.ToArray();
        return new() { Id = id, Name = "Fixture", Version = version, DownloadUrl = "https://packages.test/fixture.zip", Sha256 = Convert.ToHexString(SHA256.HashData(_download)), Size = _download.Length, SupportedArchitectures = [PortablePluginCatalog.Architecture] };
    }

    [Fact]
    public async Task InstallRestartUninstallAndReinstallPreserveUserData()
    {
        var store = Store(); await store.InitializeAsync();
        Assert.False(await store.InstallAsync(Entry()));
        Host.SetSetting("preference", "keep me");
        var path = store.Resolve(Id);
        var restarted = Store(); await restarted.InitializeAsync();
        Assert.Equal(path, restarted.Resolve(Id));
        await restarted.UninstallAsync(Id);
        Assert.Empty(restarted.Inventory());
        Assert.Throws<InvalidOperationException>(() => restarted.Resolve(Id));
        var again = Store(); await again.InitializeAsync();
        Assert.False(Directory.Exists(path));
        Assert.Equal("keep me", Host.GetSetting<string>("preference"));
        await again.InstallAsync(Entry()); Assert.True(again.IsInstalled(Id));
    }

    [Fact]
    public async Task BootstrapRunsOnceAndDoesNotResurrectRemovedPlugins()
    {
        var bundled = Path.Combine(_root, "bundled"); Directory.CreateDirectory(bundled);
        Directory.Move(Package(), Path.Combine(bundled, Id));
        var store = Store(); await store.InitializeAsync(bundled);
        Assert.True(store.IsInstalled(Id));
        await store.UninstallAsync(Id);
        var restarted = Store(); await restarted.InitializeAsync(bundled);
        Assert.Empty(restarted.Inventory());
    }

    private string BootstrapBundles(bool corruptFuture = false)
    {
        var bundled = Path.Combine(_root, "bundled"); Directory.CreateDirectory(bundled);
        Directory.Move(Package(), Path.Combine(bundled, Id));
        var future = Path.Combine(bundled, "com.test.future"); Directory.CreateDirectory(future);
        if (corruptFuture) File.WriteAllText(Path.Combine(future, "manifest.json"), "broken");
        else
        {
            File.Copy(typeof(LifecycleProbePlugin).Assembly.Location, Path.Combine(future, "plugin.dll"));
            File.WriteAllText(Path.Combine(future, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
                { Id = "com.test.future", Name = "Future", Version = "1.0.0", AssemblyName = "plugin.dll", PluginClass = typeof(LifecycleProbePlugin).FullName! }));
        }
        return bundled;
    }

    [Fact]
    public async Task DefaultBootstrapStillImportsEveryValidBundle()
    {
        var store = Store(); await store.InitializeAsync(BootstrapBundles());
        Assert.Equal(new[] { Id, "com.test.future" }.Order(), store.Inventory().Select(item => item.Manifest!.Id).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedBootstrapIgnoresFutureOrMalformedUnselectedPackages(bool corruptFuture)
    {
        var store = Store();
        await store.InitializeAsync(BootstrapBundles(corruptFuture), bootstrapPluginIds: [Id, "com.test.unknown"]);
        Assert.Equal(Id, Assert.Single(store.Inventory()).Manifest!.Id);
        Assert.Single(Directory.GetDirectories(Path.Combine(store.Root, "packages")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingIndexNeverReceivesNewBootstrapSelections(bool initiallyEmpty)
    {
        var bundles = BootstrapBundles();
        var store = Store(); await store.InitializeAsync(bundles, bootstrapPluginIds: initiallyEmpty ? [] : [Id]);
        var before = File.ReadAllText(Path.Combine(store.Root, "installed.json"));
        var restarted = Store();
        await restarted.InitializeAsync(bundles, bootstrapPluginIds: [Id, "com.test.future"]);
        Assert.Equal(initiallyEmpty ? 0 : 1, restarted.Inventory().Count);
        Assert.Equal(before, File.ReadAllText(Path.Combine(store.Root, "installed.json")));
    }

    [Fact]
    public async Task SelectedMalformedBundleStillFailsWithoutCommittingAnIndex()
    {
        var store = Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(BootstrapBundles(true), bootstrapPluginIds: ["com.test.future"]));
        Assert.False(File.Exists(Path.Combine(store.Root, "installed.json")));
    }

    [Fact]
    public async Task UpdatePreservesRunningVersionUntilRestart()
    {
        var store = Store(); await store.InitializeAsync(); await store.InstallAsync(Entry());
        var previous = store.Resolve(Id);
        Assert.True(await store.InstallAsync(Entry("1.1.0")));
        Assert.Equal(previous, store.Resolve(Id)); Assert.True(store.PendingRestart(Id));
        var restarted = Store(); await restarted.InitializeAsync();
        Assert.Equal("1.1.0", restarted.InstalledVersion(Id));
        Assert.False(restarted.PendingRestart(Id)); Assert.NotEqual(previous, restarted.Resolve(Id));
    }

    [Fact]
    public async Task RemovingPendingUpdateDoesNotBringItBackOnRestart()
    {
        var store = Store(); await store.InitializeAsync(); await store.InstallAsync(Entry());
        await store.InstallAsync(Entry("1.1.0")); await store.UninstallAsync(Id);
        var restarted = Store(); await restarted.InitializeAsync(); Assert.Empty(restarted.Inventory());
    }

    [Fact]
    public async Task BadChecksumLeavesInstalledVersionIntact()
    {
        var store = Store(); await store.InitializeAsync(); await store.InstallAsync(Entry());
        var entry = Entry("1.1.0") with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(entry));
        Assert.Equal("1.0.0", store.InstalledVersion(Id)); Assert.False(store.PendingRestart(Id));
    }

    [Fact]
    public async Task DamagedPendingUpdateKeepsPreviousVersionAndPersistentFeedback()
    {
        var store = Store(); await store.InitializeAsync(); await store.InstallAsync(Entry());
        var previous = store.Resolve(Id);
        await store.InstallAsync(Entry("1.1.0"));
        var pending = Directory.GetDirectories(Path.Combine(store.Root, "packages")).Single(path => path != previous);
        File.WriteAllText(Path.Combine(pending, "manifest.json"), "broken update");
        var restarted = Store(); await restarted.InitializeAsync();
        Assert.Equal(previous, restarted.Resolve(Id));
        Assert.Equal("1.0.0", restarted.InstalledVersion(Id));
        Assert.False(restarted.PendingRestart(Id));
        Assert.Contains("previous version", restarted.UpdateWarning(Id));
        var again = Store(); await again.InitializeAsync();
        Assert.Equal(restarted.UpdateWarning(Id), again.UpdateWarning(Id));
        await again.InstallAsync(Entry("1.1.0"));
        Assert.Null(again.UpdateWarning(Id));
        var recovered = Store(); await recovered.InitializeAsync();
        Assert.Equal("1.1.0", recovered.InstalledVersion(Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestOwnsRequiredBundledDependencies(bool includeDependency)
    {
        var bundled = Path.Combine(_root, "bundled");
        var parent = Path.Combine(bundled, Id);
        Directory.CreateDirectory(bundled);
        Directory.Move(Package(), parent);
        var manifest = PortablePluginPackage.ReadManifest(parent) with { BundledDependencies = ["com.test.dependency"] };
        File.WriteAllText(Path.Combine(parent, "manifest.json"), JsonSerializer.Serialize(manifest));
        if (includeDependency)
        {
            var dependency = Path.Combine(parent, "Dependencies", "com.test.dependency");
            Directory.CreateDirectory(Path.GetDirectoryName(dependency)!);
            Directory.Move(Package(), dependency);
            var dependencyManifest = PortablePluginPackage.ReadManifest(dependency) with { Id = "com.test.dependency", IsInternalDependency = true };
            File.WriteAllText(Path.Combine(dependency, "manifest.json"), JsonSerializer.Serialize(dependencyManifest));
        }
        var store = Store();
        if (!includeDependency)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(bundled));
            Assert.False(store.Initialized);
            Assert.False(store.IsInstalled(Id));
            return;
        }
        await store.InitializeAsync(bundled);
        Assert.Single(store.Inventory());
        Assert.True(File.Exists(Path.Combine(store.Resolve(Id), "Dependencies", "com.test.dependency", "manifest.json")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("com.test.lifecycle")]
    public async Task InvalidDependencyDeclarationsNeverCommitAnInstallation(string dependency)
    {
        var bundled = Path.Combine(_root, "bundled"); Directory.CreateDirectory(bundled);
        var parent = Path.Combine(bundled, Id); Directory.Move(Package(), parent);
        var manifest = PortablePluginPackage.ReadManifest(parent) with { BundledDependencies = [dependency] };
        File.WriteAllText(Path.Combine(parent, "manifest.json"), JsonSerializer.Serialize(manifest));
        var store = Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(bundled));
        Assert.False(store.Initialized);
    }

    [Fact]
    public async Task InternalDependenciesAreNotIndependentBootstrapIntegrations()
    {
        var bundled = Path.Combine(_root, "bundled"); Directory.CreateDirectory(bundled);
        var parent = Path.Combine(bundled, Id); Directory.Move(Package(), parent);
        var manifest = PortablePluginPackage.ReadManifest(parent) with { IsInternalDependency = true };
        File.WriteAllText(Path.Combine(parent, "manifest.json"), JsonSerializer.Serialize(manifest));
        var store = Store(); await store.InitializeAsync(bundled);
        Assert.Empty(store.Inventory());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("file.dll:stream")]
    public async Task ArchiveTraversalNeverRegistersPlugin(string path)
    {
        var store = Store(); await store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(Entry(unsafePath: path)));
        Assert.Empty(store.Inventory()); Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task WrongIdentityAndArchitectureAreRejected()
    {
        var store = Store(); await store.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(Entry() with { Id = "com.test.other" }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(Entry() with { SupportedArchitectures = ["unavailable"] }));
        Assert.Empty(store.Inventory());
    }

    [Fact]
    public async Task CancelledInstallDoesNotChangeInventory()
    {
        var store = Store(); await store.InitializeAsync(); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InstallAsync(Entry(), ct: ct.Token));
        Assert.Empty(store.Inventory());
    }

    [Fact]
    public async Task LifecycleHooksDoNotActivateAndUnloadPrecedesUninstall()
    {
        var reports = new List<PluginInstallationProgress>();
        var progress = new ImmediateProgress(reports.Add);
        var store = Store(hooks: true); await store.InitializeAsync(); await store.InstallAsync(Entry(), progress);
        Assert.Contains(reports, item => item.Message == "Preparing fixture resources" && item.Fraction == 0.5);
        Assert.True(Host.GetSetting<bool>("installed")); Assert.False(Host.GetSetting<bool>("active"));
        await using (var runtime = await PortablePluginPackage.LoadAsync(store.Resolve(Id), Host, new(1, 1, 0)))
            Assert.True(Host.GetSetting<bool>("active"));
        Assert.True(Host.GetSetting<bool>("unloaded"));
        await store.UninstallAsync(Id, progress); Assert.True(Host.GetSetting<bool>("removed"));
        Assert.Contains(reports, item => item.Message == "Releasing fixture registrations" && item.Fraction is null);
    }

    [Fact]
    public async Task FailedLifecycleHooksKeepInventoryConsistent()
    {
        var store = Store(hooks: true); await store.InitializeAsync(); Host.SetSetting("fail-install", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(Entry())); Assert.Empty(store.Inventory());
        Host.SetSetting("fail-install", false); await store.InstallAsync(Entry()); Host.SetSetting("fail-uninstall", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UninstallAsync(Id)); Assert.True(store.IsInstalled(Id));
    }

    [Fact]
    public async Task CatalogUsesOnlyV2AndRejectsDuplicateIds()
    {
        var entry = Entry(); _download = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { plugins = new[] { entry } }));
        var catalog = new PortablePluginCatalog(_http);
        Assert.Equal("https://typewhisper.github.io/typewhisper-win/plugins-v2.json", catalog.Feed.AbsoluteUri);
        Assert.Single(await catalog.FetchAsync());
        _download = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { entry, entry }));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.FetchAsync());
    }

    private sealed class ImmediateProgress(Action<PluginInstallationProgress> report) : IProgress<PluginInstallationProgress>
    {
        public void Report(PluginInstallationProgress value) => report(value);
    }

    private sealed class Handler(Func<byte[]> bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes()) });
    }
}
