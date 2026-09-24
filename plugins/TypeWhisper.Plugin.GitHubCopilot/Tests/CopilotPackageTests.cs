using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using GitHub.Copilot;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.GitHubCopilot.Tests;

public sealed class CopilotPackageTests
{
    private const string Id = "com.typewhisper.github-copilot";
    private static string PackageSource => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", Id));

    [Fact]
    public async Task CompletePackageInstallsLoadsRestartsAndReinstallsThroughHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "copilot-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var file in new[] { "GitHub.Copilot.SDK.dll", "Microsoft.Extensions.AI.Abstractions.dll", "Microsoft.Extensions.Logging.Abstractions.dll",
                "Microsoft.Extensions.DependencyInjection.Abstractions.dll", "TypeWhisper.Plugin.GitHubCopilot.deps.json", "Localization/en.json", "Localization/de.json",
                "Licenses/GitHub.Copilot.CLI.txt", "Licenses/GitHub.Copilot.SDK.txt" })
                Assert.True(File.Exists(Path.Combine(PackageSource, file)), file);
            Assert.False(File.Exists(Path.Combine(PackageSource, "TypeWhisper.PluginSDK.dll")));
            Assert.True(File.Exists(Path.Combine(PackageSource, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                "native", OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime")));
            var archive = Path.Combine(root, "copilot.zip");
            ZipFile.CreateFromDirectory(PackageSource, archive);
            var payload = await File.ReadAllBytesAsync(archive);
            using var http = new HttpClient(new PackageHandler(payload));
            var host = new VocabularyHostServices(Path.Combine(root, "data"));
            host.SetSetting("selectedModel", "saved-model");
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http, _ => host);
            var store = Store(); await store.InitializeAsync(); await store.InstallAsync(Entry(payload));
            var firstPath = store.Resolve(Id);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                store = Store(); await store.InitializeAsync();
                if (cycle == 2) await store.InstallAsync(Entry(payload));
                await using var registry = new PortablePluginRuntimeRegistry(store, new(1, 1, 2), _ => host);
                await registry.InitializeAsync();
                Assert.Null(await registry.SetEnabledAsync(Id, true));
                var provider = Assert.Single(registry.LlmProviders);
                Assert.False(provider.Ready);
                Assert.Equal("GitHub Copilot", provider.Name);
                await registry.UseConfigurationAsync(Id, (plugin, _) =>
                {
                    Assert.IsAssignableFrom<IPluginSettingsActions>(plugin);
                    Assert.False(plugin is IApiKeyPlugin);
                    Assert.IsAssignableFrom<IPluginProfileSettings>(plugin);
                    Assert.Equal("saved-model", ((IPluginTextSettings)plugin).TextSettings.Single(f => f.Id.EndsWith("/model", StringComparison.Ordinal)).Value);
                    Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase");
                    return Task.FromResult(true);
                });
                await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UseLlmAsync(provider.SelectionId,
                    (plugin, ct) => plugin.ProcessAsync("s", "u", "m", ct)));
                if (cycle == 0) Assert.Equal(firstPath, store.Resolve(Id));
                if (cycle == 1) { Assert.Null(await registry.SetEnabledAsync(Id, false)); await store.UninstallAsync(Id); }
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => PortablePluginPackage.LoadAsync(store.Resolve(Id), host, new(1, 1, 1)));
        }
        finally { await DeleteRootAsync(root); }
    }

    [Fact]
    public async Task RealPackageReplacesOlderMetadataFixtureOnlyAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "copilot-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "current.zip");
            ZipFile.CreateFromDirectory(PackageSource, archive);
            var current = await File.ReadAllBytesAsync(archive);
            var oldArchive = Path.Combine(root, "older-fixture.zip");
            File.Copy(archive, oldArchive);
            // Only a lower-version receipt fixture: never execute this deliberately old manifest.
            using (var zip = ZipFile.Open(oldArchive, ZipArchiveMode.Update))
            {
                zip.GetEntry("manifest.json")!.Delete();
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(PackageSource, "manifest.json")))!;
                manifest["version"] = "1.0.0";
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
                writer.Write(manifest.ToJsonString());
            }
            var older = await File.ReadAllBytesAsync(oldArchive);
            var handler = new PackageHandler(older);
            using var http = new HttpClient(handler);
            PortablePluginStore Store() => new(Path.Combine(root, "store"), new(1, 1, 2), http);
            var store = Store(); await store.InitializeAsync();
            await store.InstallAsync(Entry(older, "1.0.0"));
            var oldPath = store.Resolve(Id);
            handler.Payload = current;
            Assert.True(await store.InstallAsync(Entry(current)));
            Assert.True(store.PendingRestart(Id));
            Assert.Equal(oldPath, store.Resolve(Id));
            var restart = Store(); await restart.InitializeAsync();
            Assert.False(restart.PendingRestart(Id));
            Assert.NotEqual(oldPath, restart.Resolve(Id));
            await using var package = await PortablePluginPackage.LoadAsync(restart.Resolve(Id), new TestHost(), new(1, 1, 2));
            Assert.Equal("1.1.1", package.Plugin.PluginVersion);
        }
        finally { await DeleteRootAsync(root); }
    }

    [Fact]
    public async Task BundledRuntimeStartsWithAnIsolatedSignedOutProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "copilot-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var options = CopilotTransport.CreateClientOptions(root);
            var runtime = Path.Combine(PackageSource, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native");
            options.Connection = RuntimeConnection.ForStdio(Path.Combine(runtime, OperatingSystem.IsWindows() ? "copilot-runtime.exe" : "copilot-runtime"));
            options.UseLoggedInUser = false;
            options.BaseDirectory = root;
            options.WorkingDirectory = root;
            options.Environment = new Dictionary<string, string>
            {
                ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? "",
                ["PATH"] = Environment.GetEnvironmentVariable("SystemRoot") is { } system ? Path.Combine(system, "System32") : "/usr/bin:/bin",
                ["HOME"] = root, ["USERPROFILE"] = root, ["APPDATA"] = root, ["LOCALAPPDATA"] = root,
                ["TEMP"] = root, ["TMP"] = root, ["COPILOT_DISABLE_KEYTAR"] = "1"
            };
            await using var client = new CopilotClient(options);
            try
            {
                await client.StartAsync(timeout.Token);
                Assert.False((await client.GetAuthStatusAsync(timeout.Token)).IsAuthenticated);
            }
            finally { await client.ForceStopAsync(); }
        }
        finally { await DeleteRootAsync(root); }
    }

    private static PortableCatalogEntry Entry(byte[] payload, string version = "1.1.1") => new()
    {
        Id = Id, Name = "GitHub Copilot", Version = version, MinHostVersion = "1.1.2",
        DownloadUrl = "https://fixture.invalid/copilot.zip", Size = payload.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(payload)), SupportedArchitectures = [PortablePluginCatalog.Architecture]
    };

    private static async Task DeleteRootAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Collectible load contexts release native file handles after finalization.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { Directory.Delete(root, true); return; }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            { await Task.Delay(100); }
        }
    }

    private sealed class PackageHandler(byte[] payload) : HttpMessageHandler
    {
        internal byte[] Payload = payload;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(Payload) });
    }
}
