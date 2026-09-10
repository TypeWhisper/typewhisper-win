using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.Plugin.OpenAi;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public partial class OpenAiPluginTests
{
    [Fact]
    public async Task IsolatedPackageInstallsRestartsAndReinstallsWithAllRoles()
    {
        var root = Path.Combine(Path.GetTempPath(), "openai-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", "com.typewhisper.openai"));
            Assert.True(File.Exists(Path.Combine(source, "TypeWhisper.Plugin.OpenAi.deps.json")));
            foreach (var dependency in new[] { "NAudio.Core.dll", "NAudio.Wasapi.dll", "NAudio.WinMM.dll", "Localization/en.json", "Localization/de.json" })
                Assert.True(File.Exists(Path.Combine(source, dependency)), dependency);
            Assert.False(File.Exists(Path.Combine(source, "TypeWhisper.PluginSDK.dll")));
            var archive = Path.Combine(root, "openai.zip");
            ZipFile.CreateFromDirectory(source, archive);
            var bytes = await File.ReadAllBytesAsync(archive);
            using var http = new HttpClient(new CapturingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) })));
            var host = new TestPluginHostServices();
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.openai", Name = "OpenAI", Version = "1.1.4", MinHostVersion = "1.1.2",
                DownloadUrl = "https://fixture.invalid/openai.zip", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SupportedArchitectures = [PortablePluginCatalog.Architecture]
            };
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
            var firstPath = store.Resolve(entry.Id);
            await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.Empty(registry.TranscriptionProviders);
                Assert.Null(await registry.SetEnabledAsync(entry.Id, true));
                Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
                Assert.False(Assert.Single(registry.TtsProviders).Ready);
                await registry.UseConfigurationAsync(entry.Id, async (plugin, ct) =>
                {
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase" or "System.Windows.Forms");
                    Assert.IsAssignableFrom<IPluginSettingsActions>(plugin);
                    await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture-key");
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync("liveDelay", "high", ct);
                    return true;
                });
                await registry.RefreshCapabilitiesAsync();
                Assert.True(Assert.Single(registry.LlmProviders).Ready);
                Assert.Equal(13, Assert.Single(registry.TtsProviders).Voices.Count);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry));
                Assert.Equal(firstPath, store.Resolve(entry.Id));
            }
            var restart = Store(); await restart.InitializeAsync(); Assert.Equal(firstPath, restart.Resolve(entry.Id));
            await using (var registry = new PortablePluginRuntimeRegistry(restart, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.True(Assert.Single(registry.TranscriptionProviders).Ready);
                Assert.Equal("high", await registry.UseConfigurationAsync(entry.Id, (plugin, _) => Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "liveDelay").Value)));
                Assert.Null(await registry.SetEnabledAsync(entry.Id, false)); await restart.UninstallAsync(entry.Id);
            }
            var reinstall = Store(); await reinstall.InitializeAsync(); await reinstall.InstallAsync(entry);
            await using var final = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 2), _ => host);
            Assert.Null(await final.SetEnabledAsync(entry.Id, true)); Assert.True(Assert.Single(final.TtsProviders).Ready);
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(reinstall.Resolve(entry.Id), host, new(1, 1, 1)));
        }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
