using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TypeWhisper.Plugin.Games;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

public sealed class PortablePackageTests : IDisposable
{
    // The plugin API version that brought dictation targets; 1.2.2 offers 1.1.6.
    private static readonly Version TargetHost = new(1, 1, 7);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "portable-games-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Manifest_MatchesThePlugin_AndNeedsTheDictationTargetHost()
    {
        var manifest = PortablePluginPackage.ReadManifest(ProjectDirectory);
        using var plugin = new GamesPlugin();

        Assert.Equal(plugin.PluginId, manifest.Id);
        Assert.Equal(plugin.PluginVersion, manifest.Version);
        Assert.Equal(typeof(GamesPlugin).FullName, manifest.PluginClass);
        Assert.Equal(TargetHost, Version.Parse(manifest.MinHostVersion!));
        Assert.Equal(PluginLocality.Local, manifest.ResolvedLocality);
    }

    [Fact]
    public void Localization_TranslatesEveryEnglishString()
    {
        var directory = Path.Combine(ProjectDirectory, "Localization");
        var english = Read(Path.Combine(directory, "en.json"));
        Assert.All(english, pair => Assert.Equal(pair.Key, pair.Value));
        foreach (var language in new[] { "de", "ja", "zh-Hans" })
        {
            var translated = Read(Path.Combine(directory, language + ".json"));
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
            Assert.All(translated, pair => Assert.Equal(pair.Key.Contains("{0}"), pair.Value.Contains("{0}")));
        }
    }

    [Fact]
    public void Localization_CoversEveryStringThePluginShows()
    {
        var english = Read(Path.Combine(ProjectDirectory, "Localization", "en.json"));
        var source = File.ReadAllText(Path.Combine(ProjectDirectory, "GamesPlugin.cs"));
        var shown = System.Text.RegularExpressions.Regex.Matches(source, "\\bL\\(\"((?:[^\"\\\\]|\\\\.)*)\"").Select(match => match.Groups[1].Value).ToArray();

        Assert.NotEmpty(shown);
        Assert.All(shown, text => Assert.True(english.ContainsKey(text), "Missing from Localization/en.json: " + text));
    }

    [Fact]
    public async Task Package_InstallsAsADictationTarget_OnlyOnAHostThatOffersIt()
    {
        var manifest = PortablePluginPackage.ReadManifest(ProjectDirectory);
        var source = Path.Combine(ProjectDirectory, "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "portable-host", "Plugins", manifest.Id);
        var archive = Path.Combine(_root, "package.zip");
        Directory.CreateDirectory(_root);
        ZipFile.CreateFromDirectory(source, archive, CompressionLevel.Fastest, false);
        var entry = new PortableCatalogEntry
        {
            Id = manifest.Id, Name = manifest.Name, Version = manifest.Version, MinHostVersion = manifest.MinHostVersion!,
            DownloadUrl = "https://fixture.invalid/package.zip", Size = new FileInfo(archive).Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))), SupportedArchitectures = [PortablePluginCatalog.Architecture]
        };
        using var http = new HttpClient(new ArchiveTransport(archive));
        var host = new VocabularyHostServices(Path.Combine(_root, "data"));

        var older = new PortablePluginStore(Path.Combine(_root, "older"), new(1, 1, 6), http, _ => host);
        await older.InitializeAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => older.InstallAsync(entry));

        var store = new PortablePluginStore(Path.Combine(_root, "store"), TargetHost, http, _ => host);
        await store.InitializeAsync();
        await store.InstallAsync(entry);
        await using var runtime = new PortablePluginRuntimeRegistry(store, TargetHost, _ => host);
        await runtime.InitializeAsync();
        Assert.Null(await runtime.SetEnabledAsync(entry.Id, true));

        var target = Assert.Single(runtime.DictationTargets);
        Assert.True(target.Handles("WowClassic"));
        Assert.False(target.Handles("notepad"));
        Assert.Null(await runtime.UseDictationTargetAsync(target, (plugin, ct) => plugin.PrepareDictationAsync(new(Guid.NewGuid(), "notepad", null), ct)));
        var plan = await runtime.UseDictationTargetAsync(target, (plugin, ct) => plugin.PrepareDictationAsync(new(Guid.NewGuid(), "Wow", null), ct));
        Assert.Equal("World of Warcraft", plan!.AppName);
        Assert.Null(plan.TranslateTo);
        await runtime.UseConfigurationAsync(entry.Id, (plugin, _) =>
        {
            Assert.NotEmpty(((IPluginTextSettings)plugin).TextSettings);
            Assert.NotEmpty(((IPluginSettingsActions)plugin).SettingsActions);
            return Task.FromResult(true);
        });
        Assert.Null(await runtime.SetEnabledAsync(entry.Id, false));
    }

    private static string ProjectDirectory => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static Dictionary<string, string> Read(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

    public void Dispose()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ArchiveTransport(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(File.OpenRead(path)) });
        }
    }
}
