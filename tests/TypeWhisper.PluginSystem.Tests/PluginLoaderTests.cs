using System.IO;
using System.Text.Json;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginLoaderTests : IDisposable
{
    private readonly PluginLoader _loader = new();
    private readonly string _tempDir;

    public PluginLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TypeWhisperTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void DiscoverAndLoad_EmptyDirectory_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_NonExistentDirectory_ReturnsEmpty()
    {
        var nonExistent = Path.Combine(_tempDir, "does_not_exist");
        var result = _loader.DiscoverAndLoad([nonExistent]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_MultipleNonExistentDirectories_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([
            Path.Combine(_tempDir, "a"),
            Path.Combine(_tempDir, "b"),
            Path.Combine(_tempDir, "c")
        ]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_PluginDirWithoutManifest_ReturnsEmpty()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.nomanifest");
        Directory.CreateDirectory(pluginDir);

        // No manifest.json created
        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_InvalidManifestJson_ReturnsEmpty()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.badjson");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), "{ not valid json!!!");

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_ManifestWithMissingAssembly_ReturnsEmpty()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.noasm");
        Directory.CreateDirectory(pluginDir);

        var manifest = new PluginManifest
        {
            Id = "com.test.noasm",
            Name = "No Assembly",
            Version = "1.0.0",
            AssemblyName = "NonExistent.dll",
            PluginClass = "NonExistent.Plugin"
        };

        File.WriteAllText(
            Path.Combine(pluginDir, "manifest.json"),
            JsonSerializer.Serialize(manifest));

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_EmptySearchDirectories_ReturnsEmpty()
    {
        var result = _loader.DiscoverAndLoad([]);
        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_MixedValidAndInvalidDirs_SkipsBadOnes()
    {
        // Create one dir with a bad manifest, one that doesn't exist
        var badPluginDir = Path.Combine(_tempDir, "com.test.bad");
        Directory.CreateDirectory(badPluginDir);
        File.WriteAllText(Path.Combine(badPluginDir, "manifest.json"), "null");

        var result = _loader.DiscoverAndLoad([
            _tempDir,
            Path.Combine(_tempDir, "nonexistent")
        ]);

        Assert.Empty(result);
    }

    [Fact]
    public void DiscoverAndLoad_ManifestDeserializesToNull_ReturnsEmpty()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.nullmanifest");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), "null");

        var result = _loader.DiscoverAndLoad([_tempDir]);
        Assert.Empty(result);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }
}
