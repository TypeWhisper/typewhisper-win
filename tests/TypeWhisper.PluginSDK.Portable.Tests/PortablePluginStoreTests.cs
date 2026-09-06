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
    private string Package(string version = "1.0.0")
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        File.Copy(typeof(LifecycleProbePlugin).Assembly.Location, Path.Combine(folder, "plugin.dll"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new PluginManifest
        { Id = Id, Name = "Fixture", Version = version, AssemblyName = "plugin.dll", PluginClass = typeof(LifecycleProbePlugin).FullName! }));
        return folder;
    }
    private PortableCatalogEntry Entry(string version = "1.0.0", string? unsafePath = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var file in Directory.GetFiles(Package(version))) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            if (unsafePath is not null) { using var output = new StreamWriter(zip.CreateEntry(unsafePath).Open()); output.Write("unsafe"); }
        }
        _download = stream.ToArray();
        return new() { Id = Id, Name = "Fixture", Version = version, DownloadUrl = "https://packages.test/fixture.zip", Sha256 = Convert.ToHexString(SHA256.HashData(_download)), Size = _download.Length, SupportedArchitectures = [PortablePluginCatalog.Architecture] };
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
