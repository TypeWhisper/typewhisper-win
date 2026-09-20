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
        Assert.Equal("1.1.2", manifest.MinHostVersion);
        Assert.True(manifest.IsLocal);
        Assert.Contains("transcription", manifest.Categories!);
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
            Directory.CreateDirectory(Path.Join(assetDirectory, "managed-runtime", "hf-cache"));
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "settings.json"), "preserve");
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "managed-runtime", ".setup-complete"), "ready");
            await File.WriteAllTextAsync(Path.Join(assetDirectory, "managed-runtime", "hf-cache", "weights.bin"), "model");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(assetDirectory));

            await sut.RemoveModelAsync("granite-4.0-1b-speech", CancellationToken.None);

            Assert.True(sut.SupportsModelRemoval);
            Assert.False(Directory.Exists(Path.Join(assetDirectory, "managed-runtime")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Join(assetDirectory, "settings.json")));
            Assert.False(sut.IsModelDownloaded("granite-4.0-1b-speech"));
        }
        finally
        {
            if (Directory.Exists(assetDirectory))
                Directory.Delete(assetDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveModelAsync_RequiresActivatedHostBeforeRecursiveDelete()
    {
        using var sut = new GraniteSpeechPlugin();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RemoveModelAsync("granite-4.0-1b-speech", CancellationToken.None));

        Assert.Equal("Plugin is not activated.", error.Message);
    }

    [Fact]
    public async Task ProcessingDevicePersistsAndInvalidValuesDoNotChangeIt()
    {
        var host = new FakePluginHostServices(Path.GetTempPath());
        using var sut = new GraniteSpeechPlugin();
        await sut.ActivateAsync(host);
        Assert.False(sut.IsConfigured);
        Assert.True(sut.SupportsLocalLivePreview);
        Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(sut);
        await sut.SaveTextSettingAsync("device", "NvidiaCuda", default);
        await sut.DeactivateAsync();
        await sut.ActivateAsync(host);
        Assert.Equal("NvidiaCuda", Assert.Single(sut.TextSettings).Value);
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SaveTextSettingAsync("device", "unknown", default));
        Assert.Equal("NvidiaCuda", Assert.Single(sut.TextSettings).Value);
        Assert.False(sut.IsModelDownloaded("unknown"));
    }

    [Fact]
    public async Task InvalidPcmAndCanceledCallsNeverStartALocalProcess()
    {
        using var sut = new GraniteSpeechPlugin();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.TranscribePcmAsync(new float[] { float.NaN }, "de", false, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.TranscribePcmAsync(new float[160], "de", false, canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.TranscribeAsync([], "xx", false, null, default));
        Assert.Equal("Model not loaded", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task RuntimeReadinessRequiresPatchedPackagesAndSupportsDeviceChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "granite-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "managed-runtime"));
            var marker = Path.Combine(root, "managed-runtime", ".setup-complete");
            var cachedModel = Path.Combine(root, "managed-runtime", "cached-model");
            await File.WriteAllTextAsync(cachedModel, "keep");
            await File.WriteAllTextAsync(marker, "old-runtime");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(root));
            Assert.False(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "Cpu", default);
            await File.WriteAllTextAsync(marker, GraniteSpeechPlugin.RuntimeRevision + "|cpu");
            Assert.True(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "NvidiaCuda", default);
            Assert.False(sut.IsConfigured);
            Assert.Equal("keep", await File.ReadAllTextAsync(cachedModel));
            await File.WriteAllTextAsync(marker, GraniteSpeechPlugin.RuntimeRevision + "|cuda");
            Assert.True(sut.IsConfigured);
            await sut.SaveTextSettingAsync("device", "Cpu", default);
            Assert.True(sut.IsConfigured);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingsActionCanRemoveTheOnlySelectedModelAndPreservesSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "granite-action-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "managed-runtime"));
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "preserve");
            using var sut = new GraniteSpeechPlugin();
            await sut.ActivateAsync(new FakePluginHostServices(root));
            Assert.NotNull(sut.SelectedModelId);
            await sut.ExecuteSettingsActionAsync("remove-assets", default);
            Assert.Null(sut.SelectedModelId);
            Assert.False(Directory.Exists(Path.Combine(root, "managed-runtime")));
            Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(root, "settings.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static PluginManifest? ReadManifest() =>
        JsonSerializer.Deserialize<PluginManifest>(
            TestFile.ReadProjectFile("plugins-v2", "TypeWhisper.Plugin.GraniteSpeech", "manifest.json"),
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
        private readonly Dictionary<string, object?> _settings = [];
        public T? GetSetting<T>(string key) => _settings.TryGetValue(key, out var value) ? (T?)value : default;
        public void SetSetting<T>(string key, T value) => _settings[key] = value;
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
