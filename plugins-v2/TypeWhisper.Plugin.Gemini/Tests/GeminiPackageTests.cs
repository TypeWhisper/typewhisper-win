using System.IO.Compression;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginSystem.Tests;

public sealed partial class GeminiPluginTests
{
    [Fact]
    public async Task PackageLifecycle_InstallsConfiguresRestartsAndReinstalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "gemini-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin",
                new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", "com.typewhisper.gemini"));
            Assert.Equal(new[] { "TypeWhisper.Plugin.Gemini.deps.json", "TypeWhisper.Plugin.Gemini.dll", "manifest.json" },
                Directory.GetFiles(source).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var zip = Path.Combine(root, "gemini.zip"); ZipFile.CreateFromDirectory(source, zip);
            var bytes = await File.ReadAllBytesAsync(zip);
            using var http = new HttpClient(new CapturingHandler((request, _) => new(System.Net.HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) }));
            var host = new TestPluginHostServices();
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 5), http, _ => host);
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.gemini", Name = "Google Gemini", Version = PortablePluginPackage.ReadManifest(source).Version, MinHostVersion = "1.1.5",
                DownloadUrl = "https://fixture.invalid/gemini.zip", Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SupportedArchitectures = [PortablePluginCatalog.Architecture]
            };
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
            var firstPath = store.Resolve(entry.Id);
            await using (var runtime = new PortablePluginRuntimeRegistry(store, new(1, 1, 5), _ => host))
            {
                await runtime.InitializeAsync(); Assert.Empty(runtime.LlmProviders);
                Assert.Null(await runtime.SetEnabledAsync(entry.Id, true));
                Assert.False(Assert.Single(runtime.LlmProviders).Ready);
                Assert.Single(runtime.TranscriptionProviders); Assert.Empty(runtime.TtsProviders);
                await runtime.UseConfigurationAsync(entry.Id, async (plugin, ct) =>
                {
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase");
                    await ((IApiKeyPlugin)plugin).SetApiKeyAsync("fixture-key");
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync("selectedLlmModel", "gemini-pro-latest", ct);
                    await ((IPluginTextSettings)plugin).SaveTextSettingAsync("transcriptionMode", "verbatim", ct);
                    return true;
                });
                await runtime.RefreshCapabilitiesAsync(); Assert.True(Assert.Single(runtime.LlmProviders).Ready);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry));
            }
            var restart = Store(); await restart.InitializeAsync(); Assert.Equal(firstPath, restart.Resolve(entry.Id));
            await using (var runtime = new PortablePluginRuntimeRegistry(restart, new(1, 1, 5), _ => host))
            {
                await runtime.InitializeAsync(); Assert.True(Assert.Single(runtime.LlmProviders).Ready);
                Assert.Equal("gemini-pro-latest", await runtime.UseConfigurationAsync(entry.Id, (plugin, _) =>
                    Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "selectedLlmModel").Value)));
                Assert.Null(await runtime.SetEnabledAsync(entry.Id, false)); await restart.UninstallAsync(entry.Id);
            }
            var reinstall = Store(); await reinstall.InitializeAsync(); await reinstall.InstallAsync(entry);
            await using var final = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 5), _ => host);
            Assert.Null(await final.SetEnabledAsync(entry.Id, true)); Assert.True(Assert.Single(final.LlmProviders).Ready);
            Assert.Equal("verbatim", await final.UseConfigurationAsync(entry.Id, (plugin, _) =>
                Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "transcriptionMode").Value)));
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(reinstall.Resolve(entry.Id), host, new(1, 1, 4)));
        }
        finally
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
