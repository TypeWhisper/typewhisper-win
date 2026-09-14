using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public partial class AssemblyAiTests
{
    [Fact]
    public async Task IndependentPackageInstallsConfiguresRestartsUninstallsAndReinstalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "assemblyai-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", "com.typewhisper.assemblyai"));
            Assert.Equal(new[] { "TypeWhisper.Plugin.AssemblyAi.deps.json", "TypeWhisper.Plugin.AssemblyAi.dll", "manifest.json" }, Directory.GetFiles(source).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var archive = Path.Combine(root, "assemblyai.zip"); ZipFile.CreateFromDirectory(source, archive);
            var bytes = await File.ReadAllBytesAsync(archive);
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) })));
            var host = new TestPluginHostServices();
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.assemblyai", Name = "AssemblyAI", Version = "1.1.0", MinHostVersion = "1.1.2",
                DownloadUrl = "https://fixture.invalid/assemblyai.zip", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SupportedArchitectures = [PortablePluginCatalog.Architecture]
            };
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
            var firstPath = store.Resolve(entry.Id);
            await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.Empty(registry.TranscriptionProviders);
                Assert.Null(await registry.SetEnabledAsync(entry.Id, true));
                Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
                await registry.UseConfigurationAsync(entry.Id, async (plugin, ct) =>
                {
                    Assert.Equal(entry.Version, plugin.PluginVersion);
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase" or "System.Windows.Forms");
                    Assert.IsAssignableFrom<IPluginSettingsActions>(plugin);
                    await ((IPluginProfileSettings)plugin).SaveProfileSettingsAsync("assemblyai",
                        new Dictionary<string, string> { ["selectedModel"] = "universal-2", ["speakerDiarizationEnabled"] = "true" }, "fixture-key", ct);
                    return true;
                });
                await registry.RefreshCapabilitiesAsync(); Assert.True(Assert.Single(registry.TranscriptionProviders).Ready);
                Assert.Empty(registry.LlmProviders); Assert.Empty(registry.TtsProviders);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry)); Assert.Equal(firstPath, store.Resolve(entry.Id));
            }
            var restart = Store(); await restart.InitializeAsync(); Assert.Equal(firstPath, restart.Resolve(entry.Id));
            await using (var registry = new PortablePluginRuntimeRegistry(restart, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.True(Assert.Single(registry.TranscriptionProviders).Ready);
                await registry.UseConfigurationAsync(entry.Id, (plugin, _) =>
                {
                    var engine = (ITranscriptionEnginePlugin)plugin;
                    Assert.Equal("universal-2", engine.SelectedModelId); Assert.False(engine.SupportsStreaming);
                    return Task.FromResult(true);
                });
                Assert.Null(await registry.SetEnabledAsync(entry.Id, false)); await restart.UninstallAsync(entry.Id);
                Assert.False(restart.IsInstalled(entry.Id)); Assert.Single(host.Secrets);
            }
            var reinstall = Store(); await reinstall.InitializeAsync(); await reinstall.InstallAsync(entry);
            await using var final = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 2), _ => host);
            Assert.Null(await final.SetEnabledAsync(entry.Id, true)); Assert.True(Assert.Single(final.TranscriptionProviders).Ready);
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(reinstall.Resolve(entry.Id), host, new(1, 1, 1)));
        }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
