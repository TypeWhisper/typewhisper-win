using System.IO;
using System.Text.Json;
using TypeWhisper.Plugin.GraniteSpeech;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class GraniteSpeechPluginTests
{
    [Fact]
    public void Manifest_TargetsTypeWhisper084OrNewer()
    {
        var manifest = ReadManifest();

        Assert.NotNull(manifest);
        Assert.Equal("com.typewhisper.granite-speech", manifest.Id);
        Assert.Equal("0.8.4", manifest.MinHostVersion);
    }

    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var manifest = ReadManifest();
        var sut = new GraniteSpeechPlugin();

        Assert.NotNull(manifest);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Fact]
    public async Task RemoveModelAsync_DeletesTheManagedAssetDirectory()
    {
        var assetDirectory = Path.Join(Path.GetTempPath(), $"tw-granite-remove-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Join(assetDirectory, "hf-cache"));
            await File.WriteAllTextAsync(Path.Join(assetDirectory, ".setup-complete"), "ready");
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "hf-cache", "weights.bin"), "model");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(assetDirectory));

            await sut.RemoveModelAsync("granite-4.0-1b-speech", CancellationToken.None);

            Assert.True(sut.SupportsModelRemoval);
            Assert.False(Directory.Exists(assetDirectory));
            Assert.False(sut.IsModelDownloaded("granite-4.0-1b-speech"));
        }
        finally
        {
            if (Directory.Exists(assetDirectory))
                Directory.Delete(assetDirectory, recursive: true);
        }
    }

    private static PluginManifest? ReadManifest() =>
        JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile("plugins", "TypeWhisper.Plugin.GraniteSpeech", "manifest.json"),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    private sealed class FakePluginHostServices(string pluginDataDirectory) : IPluginHostServices
    {
        public string PluginDataDirectory { get; } = pluginDataDirectory;
        public string? ActiveAppProcessName => null;
        public string? ActiveAppName => null;
        public IPluginEventBus EventBus { get; } = new NoOpPluginEventBus();
        public IReadOnlyList<string> AvailableProfileNames => [];
        public IPluginLocalization Localization { get; } = new NoOpPluginLocalization();

        public Task StoreSecretAsync(string key, string value) => Task.CompletedTask;
        public Task<string?> LoadSecretAsync(string key) => Task.FromResult<string?>(null);
        public Task DeleteSecretAsync(string key) => Task.CompletedTask;
        public T? GetSetting<T>(string key) => default;
        public void SetSetting<T>(string key, T value) { }
        public void Log(PluginLogLevel level, string message) { }
        public void NotifyCapabilitiesChanged() { }
    }

    private sealed class NoOpPluginEventBus : IPluginEventBus
    {
        public void Publish<T>(T pluginEvent) where T : PluginEvent { }
        public IDisposable Subscribe<T>(Func<T, Task> handler) where T : PluginEvent => new NoOpDisposable();
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NoOpPluginLocalization : IPluginLocalization
    {
        public string CurrentLanguage => "en";
        public IReadOnlyList<string> AvailableLanguages => ["en"];
        public string GetString(string key) => key;
        public string GetString(string key, params object[] args) => string.Format(key, args);
    }
}
