using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Fact]
    public async Task ApplyingCudaPreferenceDoesNotHashRuntimeFiles()
    {
        using var temp = new TempDirectory();
        var installer = new FakeCudaRuntimeInstaller(temp.Path) { IsInstalledOverride = true };
        using var plugin = new WhisperCppPlugin(installer);
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
        Assert.Equal(0, installer.IntegrityReadCount);
    }

    [Fact]
    public async Task RuntimeIntegrityVerificationHonorsCancellation()
    {
        using var temp = new TempDirectory(); using var client = new HttpClient();
        using var installer = new WhisperCppCudaRuntimeInstaller(temp.Path, client);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.VerifyInstalledAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(0, 75, null)]
    [InlineData(500000, 75, null)]
    [InlineData(74000000L, 75, 75000000L)]
    public void ImplausibleOrIncompleteModelDownloadIsRejected(long bytes, double estimate, long? expected)
    {
        Assert.Throws<InvalidDataException>(() => WhisperCppPlugin.ValidateModelDownload(bytes, estimate, expected));
    }

    [Fact]
    public void RoundedModelSizeEstimatesDoNotRequireExactLengths()
    {
        WhisperCppPlugin.ValidateModelDownload(74000000, 75, null);
    }

    [Fact]
    public async Task EmptyModelDownloadIsNotPublished()
    {
        using var temp = new TempDirectory(); using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.OpenModelDownloadAsync = (_, _, _) => Task.FromResult<Stream>(new NonSeekableWeights([]));
        var progress = new CapturedProgress();
        await Assert.ThrowsAsync<InvalidDataException>(() => plugin.DownloadModelAsync("tiny", progress, default));
        Assert.False(plugin.IsModelDownloaded("tiny")); Assert.DoesNotContain(1.0, progress.Values);
        Assert.Empty(Directory.GetFiles(Path.Join(temp.Path, "Models")));
    }

    [Fact]
    public void CudaArchiveCleanupPreservesActiveAndUnrelatedFiles()
    {
        using var temp = new TempDirectory(); using var client = new HttpClient();
        using var installer = new WhisperCppCudaRuntimeInstaller(temp.Path, client);
        Directory.CreateDirectory(installer.RuntimeDirectory);
        var stale = Path.Join(installer.RuntimeDirectory, "nvidia-cublas-old." + Guid.NewGuid().ToString("N") + ".zip.tmp");
        var active = Path.Join(installer.RuntimeDirectory, "nvidia-cublas-current." + Guid.NewGuid().ToString("N") + ".zip.tmp");
        var other = Path.Join(installer.RuntimeDirectory, "nvidia-cublas-notes.zip.tmp");
        File.WriteAllText(stale, "partial"); File.WriteAllText(other, "notes");
        using (var download = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            installer.RemoveAbandonedDownloads();
            Assert.False(File.Exists(stale)); Assert.True(File.Exists(active)); Assert.True(File.Exists(other));
        }
        installer.RemoveAbandonedDownloads(); Assert.False(File.Exists(active));
    }

    [Fact]
    public async Task CanceledCudaExtractionDoesNotPublishFilesOrReceipt()
    {
        using var temp = new TempDirectory();
        var bytes = CreateZipArchive(("bin/cublas.dll", "valid-library"));
        var package = new WhisperCppCudaRuntimePackage("test", "https://example.test/runtime.zip",
            Convert.ToHexString(SHA256.HashData(bytes)), ["cublas.dll"]);
        using var client = new HttpClient(); using var installer = new WhisperCppCudaRuntimeInstaller(temp.Path, client, package);
        Directory.CreateDirectory(installer.RuntimeDirectory);
        var archive = Path.Join(temp.Path, "fixture.zip"); File.WriteAllBytes(archive, bytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ExtractRequiredDllsAsync(archive, cancellation.Token));
        Assert.False(installer.IsInstalled); Assert.Empty(Directory.GetFiles(installer.RuntimeDirectory));
    }

    [Fact]
    public void OldNativeCachesAreRemovedWithoutTouchingCurrentOrActiveCaches()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "1.0.0", "1.1.0", "1.2.9", "user-notes" })
        {
            Directory.CreateDirectory(Path.Join(temp.Path, name)); File.WriteAllText(Path.Join(temp.Path, name, "cache.dll"), "native");
        }
        using (var active = new FileStream(Path.Join(temp.Path, "1.1.0", "cache.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            WhisperCppPlugin.RemoveObsoleteNativeCaches(temp.Path, "1.2.9");
            Assert.False(Directory.Exists(Path.Join(temp.Path, "1.0.0")));
            Assert.True(Directory.Exists(Path.Join(temp.Path, "1.1.0")));
            Assert.True(Directory.Exists(Path.Join(temp.Path, "1.2.9")));
            Assert.True(Directory.Exists(Path.Join(temp.Path, "user-notes")));
        }
    }

    [Fact]
    public void StagedPackageContainsOnlyWindowsNativeRuntimes()
    {
        var root = Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var runtimes = Path.Join(root, "plugins-v2", "TypeWhisper.Plugin.WhisperCpp", "bin", configuration,
            "portable-host", "Plugins", "com.typewhisper.whisper-cpp", "runtimes");
        var files = Directory.GetFiles(runtimes, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        string[] supportedPrefixes = ["win-x64/", "win-x86/", "win-arm64/", "cuda/win-x64/", "vulkan/win-x64/"];
        Assert.All(files, path =>
        {
            var relative = Path.GetRelativePath(runtimes, path).Replace(Path.DirectorySeparatorChar, '/');
            Assert.Contains(supportedPrefixes, prefix => relative.StartsWith(prefix, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task ExplicitCudaLoadValidatesPersistentFilesOnlyOnce()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var assets = Path.Join(temp.Path, "cuda-assets"); Directory.CreateDirectory(assets);
        var installer = new FakeCudaRuntimeInstaller(assets) { IsInstalledOverride = true };
        using var plugin = new WhisperCppPlugin(installer)
        {
            CreateFactory = _ => (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)),
            ReleaseFactory = _ => { }
        };
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); File.WriteAllText(Path.Join(temp.Path, "Models", "ggml-tiny.bin"), "weights");
        var before = installer.IntegrityReadCount;
        await plugin.LoadModelAsync("tiny", default);
        Assert.Equal(before + 1, installer.IntegrityReadCount);
    }
}
