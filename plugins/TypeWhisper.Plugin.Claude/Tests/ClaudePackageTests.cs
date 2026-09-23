using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public partial class ClaudeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ClaudeTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task PackageInstallsConfiguresRestartsAndReinstallsWithoutLosingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin",
                new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", "com.typewhisper.claude"));
            Assert.Equal(new[] { "TypeWhisper.Plugin.Claude.deps.json", "TypeWhisper.Plugin.Claude.dll", "manifest.json" },
                Directory.GetFiles(source).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            var archive = Path.Combine(root, "claude.zip");
            ZipFile.CreateFromDirectory(source, archive);
            var bytes = await File.ReadAllBytesAsync(archive);
            using var http = new HttpClient(new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) })));
            var host = new TestPluginHostServices();
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            var entry = new PortableCatalogEntry
            {
                Id = "com.typewhisper.claude", Name = "Claude", Version = "1.1.0", MinHostVersion = "1.1.2",
                DownloadUrl = "https://fixture.invalid/claude.zip", Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SupportedArchitectures = [PortablePluginCatalog.Architecture]
            };
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(entry);
            var firstPath = store.Resolve(entry.Id);
            await using (var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.Empty(registry.LlmProviders);
                Assert.Null(await registry.SetEnabledAsync(entry.Id, true));
                Assert.False(Assert.Single(registry.LlmProviders).Ready);
                Assert.Empty(registry.TranscriptionProviders); Assert.Empty(registry.TtsProviders);
                await registry.UseConfigurationAsync(entry.Id, async (plugin, ct) =>
                {
                    Assert.Equal(entry.Version, plugin.PluginVersion);
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase" or "System.Windows.Forms");
                    await ((IPluginProfileSettings)plugin).SaveProfileSettingsAsync("claude", new Dictionary<string, string>
                    { ["llmTemperatureMode"] = "custom", ["selectedLlmModel"] = "claude-haiku-4-5-20251001" }, "fixture-key", ct);
                    return true;
                });
                await registry.RefreshCapabilitiesAsync(); Assert.True(Assert.Single(registry.LlmProviders).Ready);
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(entry));
            }
            var restart = Store(); await restart.InitializeAsync(); Assert.Equal(firstPath, restart.Resolve(entry.Id));
            await using (var registry = new PortablePluginRuntimeRegistry(restart, new(1, 1, 2), _ => host))
            {
                await registry.InitializeAsync(); Assert.True(Assert.Single(registry.LlmProviders).Ready);
                Assert.Equal("claude-haiku-4-5-20251001", await registry.UseConfigurationAsync(entry.Id, (plugin, _) =>
                    Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "selectedLlmModel").Value)));
                Assert.Null(await registry.SetEnabledAsync(entry.Id, false)); await restart.UninstallAsync(entry.Id);
            }
            var reinstall = Store(); await reinstall.InitializeAsync(); await reinstall.InstallAsync(entry);
            await using var final = new PortablePluginRuntimeRegistry(reinstall, new(1, 1, 2), _ => host);
            Assert.Null(await final.SetEnabledAsync(entry.Id, true)); Assert.True(Assert.Single(final.LlmProviders).Ready);
            Assert.Equal("custom", await final.UseConfigurationAsync(entry.Id, (plugin, _) =>
                Task.FromResult(((IPluginTextSettings)plugin).TextSettings.Single(s => s.Id == "llmTemperatureMode").Value)));
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(reinstall.Resolve(entry.Id), host, new(1, 1, 1)));
        }
        finally
        {
            // Collectible plugin load contexts release their Windows DLL handles after GC.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Directory.Delete(root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _output.WriteLine($"Package-test cleanup could not remove {root}: {ex.Message}");
            }
        }
    }
}
