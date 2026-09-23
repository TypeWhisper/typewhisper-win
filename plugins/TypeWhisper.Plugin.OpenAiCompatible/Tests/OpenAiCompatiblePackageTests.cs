using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.Plugin.OpenAiCompatible;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests;

public partial class OpenAiCompatiblePluginTests
{
    [Fact]
    public async Task IsolatedPackageInstallsRestartsAndReinstallsWithAllRoles()
    {
        var root = Path.Combine(Path.GetTempPath(), "openai-compatible-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", "com.typewhisper.openai-compatible"));
            Assert.True(File.Exists(Path.Combine(source, "TypeWhisper.Plugin.OpenAiCompatible.deps.json")));
            Assert.False(File.Exists(Path.Combine(source, "TypeWhisper.PluginSDK.dll")));
            var archive = Path.Combine(root, "openai-compatible.zip");
            ZipFile.CreateFromDirectory(source, archive);
            var bytes = await File.ReadAllBytesAsync(archive);
            using var http = new HttpClient(new CapturingHandler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) }));
            var host = new TestPluginHostServices();
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.openai-compatible", Name = "OpenAI Compatible", Version = "1.1.0", MinHostVersion = "1.1.2",
                DownloadUrl = "https://fixture.invalid/openai-compatible.zip", Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                SupportedArchitectures = [PortablePluginCatalog.Architecture]
            };
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
            var firstPath = store.Resolve(entry.Id);
            await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.Empty(registry.TranscriptionProviders);
                Assert.Null(await registry.SetEnabledAsync(entry.Id, true));
                Assert.False(Assert.Single(registry.TranscriptionProviders).Ready);
                Assert.False(Assert.Single(registry.LlmProviders).Ready);
                await registry.UseConfigurationAsync(entry.Id, async (plugin, ct) =>
                {
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase" or "System.Windows.Forms");
                    Assert.IsAssignableFrom<IPluginSettingsActions>(plugin);
                    await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture-key");
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync("openai-compatible/url", "http://localhost:11434", ct);
                    await ((IPluginSettingsActions)plugin).ExecuteSettingsActionAsync("add", ct);
                    var profile = ((IPluginConnectionSettings)plugin).ConnectionIdentity!;
                    Assert.NotEqual("openai-compatible", profile);
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync(profile + "/url", "http://localhost:1234", ct);
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync(profile + "/text", "local-chat", ct);
                    return true;
                });
                await registry.RefreshCapabilitiesAsync();
                Assert.Equal(2, registry.LlmProviders.Count);
                Assert.All(registry.LlmProviders, p => Assert.True(p.Ready));
                Assert.Empty(registry.TtsProviders);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry));
                Assert.Equal(firstPath, store.Resolve(entry.Id));
            }
            var restart = Store(); await restart.InitializeAsync(); Assert.Equal(firstPath, restart.Resolve(entry.Id));
            await using (var registry = new PortablePluginRuntimeRegistry(restart, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.Equal(2, registry.TranscriptionProviders.Count);
                Assert.All(registry.TranscriptionProviders, p => Assert.True(p.Ready));
                Assert.Equal("http://localhost:11434", await registry.UseConfigurationAsync(entry.Id, (plugin, _) => Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "openai-compatible/url").Value)));
                Assert.Null(await registry.SetEnabledAsync(entry.Id, false)); await restart.UninstallAsync(entry.Id);
            }
            var reinstall = Store(); await reinstall.InitializeAsync(); await reinstall.InstallAsync(entry);
            await using var final = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 2), _ => host);
            Assert.Null(await final.SetEnabledAsync(entry.Id, true)); Assert.Equal(2, final.LlmProviders.Count);
            Assert.Contains(final.LlmProviders, p => p.Models.Any(m => m.Id == "local-chat"));
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(reinstall.Resolve(entry.Id), host, new(1, 1, 1)));
        }
        finally { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
