using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.SupertonicTts;
namespace PortableMigration.Tests;

public sealed class PortablePackageTests
{
    [Fact]
    public async Task ActualHostServices_CanActivateAndReadAllSettingsWithoutWpf()
    {
        using var fixture = new PortableFixture();
        using ITypeWhisperPlugin plugin = new SupertonicTtsPlugin();
        await plugin.ActivateAsync(fixture.Host);
        try
        {
            Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase");
            if (plugin is IPluginTextSettings settings) Assert.All(settings.TextSettings, field => Assert.False(string.IsNullOrWhiteSpace(field.Id)));
            if (plugin is IPluginSettingsActions actions) Assert.All(actions.SettingsActions, action => Assert.False(string.IsNullOrWhiteSpace(action.Id)));
            Assert.False(fixture.Host.AllowLegacyDataMigration);
        }
        finally { await plugin.DeactivateAsync(); }
    }
    [Fact]
    public async Task Package_InstallsLoadsRestartsAndUninstallsIndependently()
    {
        using var fixture = new PortableFixture();
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        var manifest = PortablePluginPackage.ReadManifest(project);
        var source = Path.Combine(project,"bin",new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,"portable-host","Plugins",manifest.Id);
        var archive = Path.Combine(fixture.Root,"package.zip");
        ZipFile.CreateFromDirectory(source,archive,CompressionLevel.Fastest,false);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
        using var http = new HttpClient(new ArchiveTransport(archive));
        PortablePluginStore Store() => new(Path.Combine(fixture.Root,"store"),new(1,1,4),http,_ => fixture.Host);
        var entry = new PortableCatalogEntry { Id=manifest.Id,Name=manifest.Name,Version=manifest.Version,MinHostVersion=manifest.MinHostVersion!,
            DownloadUrl="https://fixture.invalid/package.zip",Size=new FileInfo(archive).Length,Sha256=hash,SupportedArchitectures=[PortablePluginCatalog.Architecture] };
        var store=Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
        var path=store.Resolve(entry.Id);
        await using (var runtime=new PortablePluginRuntimeRegistry(store,new(1,1,4),_ => fixture.Host))
        {
            await runtime.InitializeAsync();
            Assert.Null(await runtime.SetEnabledAsync(entry.Id,true));
            Assert.True(Assert.Single(runtime.Snapshot()).Enabled);
            await runtime.UseConfigurationAsync(entry.Id,async (plugin,ct) =>
            {
                Assert.Equal(manifest.Version,plugin.PluginVersion);
                if (plugin is IApiKeyPlugin key) { await key.SetApiKeyAsync("fixture-only"); Assert.True(key.IsConfigured); await key.SetApiKeyAsync(""); Assert.False(key.IsConfigured); }
                if (plugin is IPluginTextSettings settings) Assert.NotNull(settings.TextSettings);
                if (plugin is IPluginSettingsActions actions) Assert.NotNull(actions.SettingsActions);
                return true;
            });
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry));
        }
        var restart=Store(); await restart.InitializeAsync(); Assert.Equal(path,restart.Resolve(entry.Id));
        await using (var runtime=new PortablePluginRuntimeRegistry(restart,new(1,1,4),_ => fixture.Host))
        {
            await runtime.InitializeAsync(); Assert.True(Assert.Single(runtime.Snapshot()).Enabled);
            Assert.Null(await runtime.SetEnabledAsync(entry.Id,false)); await restart.UninstallAsync(entry.Id);
        }
        await restart.InstallAsync(entry);
        await using var package=await PortablePluginPackage.LoadAsync(restart.Resolve(entry.Id),fixture.Host,new(1,1,4));
        Assert.Equal(manifest.Id,package.Plugin.PluginId);
        Assert.IsAssignableFrom<ILocalTtsModelManagement>(package.Plugin);
        await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(restart.Resolve(entry.Id), fixture.Host, new(1,1,3)));
    }
    private sealed class ArchiveTransport(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage=request,Content=new StreamContent(File.OpenRead(path)) }); }
    }
}

internal sealed class PortableFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(),"portable-SupertonicTts-"+Guid.NewGuid().ToString("N"));
    internal VocabularyHostServices Host { get; }
    internal FixtureSecrets Secrets { get; } = new();
    internal PortableFixture() { Directory.CreateDirectory(Root);Host=new(Path.Combine(Root,"data"),secrets:Secrets); }
    public void Dispose()
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        try { Directory.Delete(Root,true); } catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
}
internal sealed class FixtureSecrets : IPluginSecretStore
{
    internal Dictionary<string,string> Values { get; } = [];
    internal bool FailWrites { get; set; }
    public Task StoreAsync(string key,string value) { if(FailWrites)throw new IOException("Fixture storage failure.");Values[key]=value;return Task.CompletedTask; }
    public Task DeleteAsync(string key) { if(FailWrites)throw new IOException("Fixture storage failure.");Values.Remove(key);return Task.CompletedTask; }
    public Task<string?> LoadAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
}
