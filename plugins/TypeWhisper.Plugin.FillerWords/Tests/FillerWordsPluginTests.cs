using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.PluginSystem.Tests;

namespace TypeWhisper.Plugin.FillerWords.Tests;

public sealed class FillerWordsPluginTests
{
    private static readonly PostProcessingContext Context = new();

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifestPath = TestFile.ProjectFile(
            "plugins", "TypeWhisper.Plugin.FillerWords", "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new FillerWordsPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public void PluginId_IsExpectedValue() =>
        Assert.Equal("com.typewhisper.filler-words", new FillerWordsPlugin().PluginId);

    [Fact]
    public void Priority_IsExpectedValue() =>
        Assert.Equal(250, new FillerWordsPlugin().Priority);

    [Fact]
    public async Task ProcessAsync_RemovesDefaultFillerWords()
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        var result = await sut.ProcessAsync("So um I think uh this works", Context, CancellationToken.None);

        Assert.Equal("So I think this works", result);
    }

    [Fact]
    public async Task ProcessAsync_UsesConfiguredWords()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "basically";

        var result = await sut.ProcessAsync("It is basically fine, um yes", Context, CancellationToken.None);

        Assert.Equal("It is fine, um yes", result);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsTextUnchanged_WhenListIsEmpty()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "   ";

        var result = await sut.ProcessAsync("So um I think", Context, CancellationToken.None);

        Assert.Equal("So um I think", result);
    }

    [Fact]
    public async Task ActivateAsync_SeedsDefaultWords()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();

        await sut.ActivateAsync(host);

        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, host.GetSetting<string>("words"));
    }

    [Fact]
    public async Task WordsText_PersistsToHostSettings()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);

        sut.Settings!.WordsText = "meh\nwelp";

        Assert.Equal("meh\nwelp", host.GetSetting<string>("words"));
        Assert.Equal(2, sut.Settings.WordCount);
    }

    [Fact]
    public async Task ActivateAsync_KeepsStoredWords()
    {
        var host = new TestPluginHostServices();
        host.SetSetting("words", "welp");
        var sut = new FillerWordsPlugin();

        await sut.ActivateAsync(host);

        Assert.Equal("welp", sut.Settings!.WordsText);
    }

    [Fact]
    public async Task ResetToDefaults_RestoresBuiltInList()
    {
        var host = new TestPluginHostServices();
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(host);
        sut.Settings!.WordsText = "welp";

        sut.Settings.ResetToDefaults();

        Assert.Equal(FillerWordsSettingsStore.DefaultWordsText, sut.Settings.WordsText);
        Assert.Equal(FillerWordFilter.DefaultFillerWords.Count, sut.Settings.WordCount);
    }

    [Fact]
    public async Task DeactivateAsync_ClearsSettings()
    {
        var sut = new FillerWordsPlugin();
        await sut.ActivateAsync(new TestPluginHostServices());

        await sut.DeactivateAsync();

        Assert.Null(sut.Settings);
        Assert.Null(sut.CreateSettingsView());
    }

    private sealed class TestPluginHostServices : IPluginHostServices
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

        private readonly Dictionary<string, JsonElement> _settings = [];

        public T? GetSetting<T>(string key) =>
            _settings.TryGetValue(key, out var value) ? value.Deserialize<T>(JsonOptions) : default;

        public void SetSetting<T>(string key, T value) =>
            _settings[key] = JsonSerializer.SerializeToElement(value, JsonOptions);

        public string PluginDataDirectory => Path.GetTempPath();
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
        public IPluginLocalization Localization { get; } = new NoOpLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
    }

    private sealed class NoOpEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpSubscription();

        private sealed class NoOpSubscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoOpLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }
}
