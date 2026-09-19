using System.Security.Cryptography;
using TypeWhisper.Plugin.WhisperCpp;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Theory]
    [InlineData("truncated")]
    [InlineData("same-length")]
    [InlineData("same-metadata")]
    [InlineData("missing-receipt")]
    public async Task CudaCacheRepairsIncompleteOrCorruptInstallation(string damage)
    {
        using var temp = new TempDirectory();
        var bytes = CreateZipArchive(("bin/cublas.dll", "valid-library"));
        var package = new WhisperCppCudaRuntimePackage("test-v1", "https://example.test/runtime.zip",
            Convert.ToHexString(SHA256.HashData(bytes)), ["cublas.dll"]);
        using var client = new HttpClient(new StaticArchiveHandler(bytes));
        using var installer = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package);
        await installer.EnsureInstalledAsync(default);
        Assert.True(installer.IsInstalled);
        var file = Path.Join(installer.RuntimeDirectory, "cublas.dll");
        var written = File.GetLastWriteTimeUtc(file);
        if (damage == "missing-receipt") File.Delete(Path.Join(installer.RuntimeDirectory, "installed.json"));
        else
        {
            File.WriteAllText(file, damage == "truncated" ? "x" : "wrong-library");
            File.SetLastWriteTimeUtc(file, damage == "same-metadata" ? written : DateTime.UtcNow.AddMinutes(1));
        }
        Assert.False(installer.IsInstalled);
        using var restarted = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package);
        Assert.False(restarted.IsInstalled);
        await restarted.EnsureInstalledAsync(default);
        Assert.True(restarted.IsInstalled);
        Assert.Equal("valid-library", File.ReadAllText(file));
    }

    [Fact]
    public async Task CudaCacheRejectsPreviousPinnedPackage()
    {
        using var temp = new TempDirectory();
        var bytes = CreateZipArchive(("bin/cublas.dll", "valid-library"));
        var package = new WhisperCppCudaRuntimePackage("test-v1", "https://example.test/runtime.zip",
            Convert.ToHexString(SHA256.HashData(bytes)), ["cublas.dll"]);
        using var client = new HttpClient(new StaticArchiveHandler(bytes));
        using var original = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package);
        await original.EnsureInstalledAsync(default);
        using var upgraded = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package with { RuntimeVersion = "test-v2" });
        Assert.False(upgraded.IsInstalled);
        await upgraded.EnsureInstalledAsync(default);
        Assert.True(upgraded.IsInstalled);
        Assert.False(original.IsInstalled);
    }

    [Fact]
    public async Task IncompleteCudaArchiveDoesNotPublishPartialDlls()
    {
        using var temp = new TempDirectory();
        var bytes = CreateZipArchive(("bin/cublas.dll", "valid-library"));
        var package = new WhisperCppCudaRuntimePackage("test-v1", "https://example.test/runtime.zip",
            Convert.ToHexString(SHA256.HashData(bytes)), ["cublas.dll", "missing.dll"]);
        using var client = new HttpClient(new StaticArchiveHandler(bytes));
        using var installer = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.EnsureInstalledAsync(default));
        Assert.False(installer.IsInstalled);
        Assert.Empty(Directory.EnumerateFiles(installer.RuntimeDirectory));
    }
}
