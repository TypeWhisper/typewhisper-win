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

    [Fact]
    public void DiscoverAndLoad_NewerMinimumHostVersion_IsSkippedWithDiagnostic()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.future");
        Directory.CreateDirectory(pluginDir);
        WriteManifest(pluginDir, "com.test.future", "Future plugin", "2.0.0");
        var loader = new PluginLoader(new Version(1, 5, 0));

        var result = loader.DiscoverAndLoad([_tempDir]);

        Assert.Empty(result);
        var issue = Assert.Single(loader.LoadIssues);
        Assert.Equal(PluginLoadIssueKind.MinimumHostVersionNotMet, issue.Kind);
        Assert.Equal("com.test.future", issue.Manifest.Id);
        Assert.Equal("2.0.0", issue.RequiredHostVersion);
        Assert.Equal("1.5.0", issue.CurrentHostVersion);
        Assert.Equal(pluginDir, issue.PluginDirectory);
    }

    [Fact]
    public void DiscoverAndLoad_InvalidMinimumHostVersion_IsSkippedWithDiagnostic()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.invalid-version");
        Directory.CreateDirectory(pluginDir);
        WriteManifest(pluginDir, "com.test.invalid-version", "Invalid version", "latest");
        var loader = new PluginLoader(new Version(1, 5, 0));

        var result = loader.DiscoverAndLoad([_tempDir]);

        Assert.Empty(result);
        var issue = Assert.Single(loader.LoadIssues);
        Assert.Equal(PluginLoadIssueKind.InvalidMinimumHostVersion, issue.Kind);
        Assert.Equal("latest", issue.RequiredHostVersion);
    }

    [Fact]
    public void DiscoverAndLoad_CompatibleMinimumHostVersion_ContinuesToAssemblyResolution()
    {
        var pluginDir = Path.Combine(_tempDir, "com.test.compatible");
        Directory.CreateDirectory(pluginDir);
        WriteManifest(pluginDir, "com.test.compatible", "Compatible plugin", "1.5.0");
        var loader = new PluginLoader(new Version(1, 5, 0));

        var result = loader.DiscoverAndLoad([_tempDir]);

        Assert.Empty(result);
        Assert.Empty(loader.LoadIssues);
    }

    private static void WriteManifest(
        string pluginDirectory,
        string pluginId,
        string pluginName,
        string minimumHostVersion)
    {
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = pluginName,
            Version = "1.0.0",
            MinHostVersion = minimumHostVersion,
            AssemblyName = "Missing.dll",
            PluginClass = "Missing.Plugin"
        };
        File.WriteAllText(
            Path.Combine(pluginDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
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
