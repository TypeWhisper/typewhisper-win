using System.IO;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class LocalModelStorageServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"tw-storage-{Guid.NewGuid():N}");

    public LocalModelStorageServiceTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_MigratesKnownAssetsAndKeepsPluginSettingsInPlace()
    {
        var oldRoot = Path.Join(_tempDir, "old-storage");
        var newRoot = Path.Join(_tempDir, "new-storage");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LocalModelStoragePath = oldRoot
        });
        Directory.CreateDirectory(Path.Join(oldRoot, "translation-en-fr"));
        await File.WriteAllTextAsync(Path.Join(oldRoot, "translation-en-fr", "config.json"), "{}");

        var whisperData = Path.Join(oldRoot, "PluginData", "com.typewhisper.whisper-cpp");
        Directory.CreateDirectory(Path.Join(whisperData, "Models"));
        await File.WriteAllTextAsync(Path.Join(whisperData, "Models", "ggml.bin"), "model");
        await File.WriteAllTextAsync(Path.Join(whisperData, "settings.json"), "settings");

        var sherpaData = Path.Join(oldRoot, "PluginData", "com.typewhisper.sherpa-onnx");
        Directory.CreateDirectory(Path.Join(sherpaData, "Runtimes", "sherpa-onnx-cuda"));
        await File.WriteAllTextAsync(Path.Join(sherpaData, "Runtimes", "sherpa-onnx-cuda", "runtime.dll"), "runtime");

        var graniteData = Path.Join(oldRoot, "PluginData", "com.typewhisper.granite-speech");
        Directory.CreateDirectory(Path.Join(graniteData, "python"));
        Directory.CreateDirectory(Path.Join(graniteData, "hf-cache"));
        await File.WriteAllTextAsync(Path.Join(graniteData, ".setup-complete"), "done");
        await File.WriteAllTextAsync(Path.Join(graniteData, "python", "python.exe"), "python");
        await File.WriteAllTextAsync(Path.Join(graniteData, "hf-cache", "model.bin"), "model");

        var unloaded = false;
        var sut = new LocalModelStorageService(settings, () => unloaded = true);

        await sut.MoveDownloadsAndUsePathAsync(newRoot);

        Assert.True(unloaded);
        Assert.Equal(newRoot, settings.Current.LocalModelStoragePath);
        Assert.True(File.Exists(Path.Join(newRoot, "translation-en-fr", "config.json")));
        Assert.True(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.whisper-cpp", "Models", "ggml.bin")));
        Assert.True(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.sherpa-onnx", "Runtimes", "sherpa-onnx-cuda", "runtime.dll")));
        Assert.True(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.granite-speech", ".setup-complete")));
        Assert.True(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.granite-speech", "python", "python.exe")));
        Assert.True(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.granite-speech", "hf-cache", "model.bin")));
        Assert.False(File.Exists(Path.Join(newRoot, "PluginData", "com.typewhisper.whisper-cpp", "settings.json")));
        Assert.True(File.Exists(Path.Join(whisperData, "settings.json")));
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_SavesTarget_WhenSourcesAreMissing()
    {
        var oldRoot = Path.Join(_tempDir, "old-storage");
        var newRoot = Path.Join(_tempDir, "new-storage");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LocalModelStoragePath = oldRoot
        });
        var sut = new LocalModelStorageService(settings);

        await sut.MoveDownloadsAndUsePathAsync(newRoot);

        Assert.Equal(newRoot, settings.Current.LocalModelStoragePath);
        Assert.True(Directory.Exists(newRoot));
    }

    [Fact]
    public async Task MoveDownloadsAndUsePathAsync_DoesNotSaveTarget_WhenDestinationIsInvalid()
    {
        var oldRoot = Path.Join(_tempDir, "old-storage");
        var targetFile = Path.Join(_tempDir, "not-a-directory");
        await File.WriteAllTextAsync(targetFile, "file");
        var settings = new FakeSettingsService(AppSettings.Default with
        {
            LocalModelStoragePath = oldRoot
        });
        var unloaded = false;
        var sut = new LocalModelStorageService(settings, () => unloaded = true);

        await Assert.ThrowsAsync<IOException>(() => sut.MoveDownloadsAndUsePathAsync(targetFile));

        Assert.False(unloaded);
        Assert.Equal(oldRoot, settings.Current.LocalModelStoragePath);
    }

    [Fact]
    public void ResolveAvailableModelStoragePath_Throws_WhenConfiguredPathIsMissing()
    {
        var missingRoot = Path.Join(_tempDir, "missing-storage");
        var settings = AppSettings.Default with
        {
            LocalModelStoragePath = missingRoot
        };

        var ex = Assert.Throws<LocalModelStorageUnavailableException>(() =>
            LocalModelStorageService.ResolveAvailableModelStoragePath(settings));

        Assert.Contains(missingRoot, ex.Message);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public void ResolveAvailablePluginAssetDirectory_CreatesPluginDirectory_WhenConfiguredRootExists()
    {
        var storageRoot = Path.Join(_tempDir, "model-storage");
        Directory.CreateDirectory(storageRoot);
        var settings = AppSettings.Default with
        {
            LocalModelStoragePath = storageRoot
        };

        var directory = LocalModelStorageService.ResolveAvailablePluginAssetDirectory(
            settings,
            "com.typewhisper.whisper-cpp");

        Assert.Equal(
            Path.Join(storageRoot, "PluginData", "com.typewhisper.whisper-cpp"),
            directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ResolveAvailablePluginAssetDirectory_UsesFinalPluginIdSegment()
    {
        var storageRoot = Path.Join(_tempDir, "model-storage");
        Directory.CreateDirectory(storageRoot);
        var settings = AppSettings.Default with
        {
            LocalModelStoragePath = storageRoot
        };
        var pathLikePluginId = Path.Join(_tempDir, "ignored", "com.typewhisper.whisper-cpp");

        var directory = LocalModelStorageService.ResolveAvailablePluginAssetDirectory(
            settings,
            pathLikePluginId);

        Assert.Equal(
            Path.Join(storageRoot, "PluginData", "com.typewhisper.whisper-cpp"),
            directory);
        Assert.True(Directory.Exists(directory));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"LocalModelStorageServiceTests cleanup failed for '{_tempDir}': {ex}");
        }
    }
}
