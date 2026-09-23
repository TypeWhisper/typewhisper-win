using Whisper.net.LibraryLoader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Fact]
    public async Task CanceledCudaInstallationRestoresStatusWithoutSettingRestartGate()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var host = new FakePluginHostServices(temp.Path);
        using var plugin = new WhisperCppPlugin(new FakeCudaRuntimeInstaller(temp.Path) { InstallException = new OperationCanceledException() });
        await plugin.ActivateAsync(host); plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var previous = plugin.AccelerationStatus;
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync("tiny", default));
        Assert.Equal(previous, plugin.AccelerationStatus); Assert.False(plugin.AccelerationStatus.RequiresRestart);
        Assert.True(host.CapabilityChangeCount > 0);
    }

    [Fact]
    public void NativeStagingRemovesOnlyAbandonedMatchingTemporaryFiles()
    {
        using var temp = new TempDirectory();
        var package = Path.Join(temp.Path, "package"); var assets = Path.Join(temp.Path, "assets");
        var cache = Path.Join(temp.Path, "cache");
        var runtime = Path.Join(package, "runtimes", "cuda", "win-x64");
        var destination = Path.Join(cache, "runtimes", "cuda", "win-x64");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(assets); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Join(runtime, "whisper.dll"), "native");
        var orphan = Path.Join(destination, "whisper.dll." + Guid.NewGuid().ToString("N") + ".tmp");
        var active = Path.Join(destination, "whisper.dll." + Guid.NewGuid().ToString("N") + ".tmp");
        var unrelated = Path.Join(destination, "whisper.dll.user.tmp");
        File.WriteAllText(orphan, "partial"); File.WriteAllText(unrelated, "keep");
        using (var writer = new FileStream(active, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
            Assert.False(File.Exists(orphan)); Assert.True(File.Exists(active)); Assert.True(File.Exists(unrelated));
            Assert.Equal("native", File.ReadAllText(Path.Join(destination, "whisper.dll")));
        }
        WhisperCppPlugin.StageCudaRuntime(package, assets, cache);
        Assert.False(File.Exists(active)); Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task ReusingAnExistingCudaInstallationDoesNotRequireRestart()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var installer = new FakeCudaRuntimeInstaller(temp.Path) { ReuseExistingInstallation = true };
        using var plugin = new WhisperCppPlugin(installer)
        {
            CreateFactory = _ => (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)),
            ReleaseFactory = _ => { }
        };
        var host = new FakePluginHostServices(temp.Path); await plugin.ActivateAsync(host);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await plugin.LoadModelAsync("tiny", default);
        Assert.Equal(1, installer.EnsureInstalledCallCount); Assert.False(plugin.AccelerationStatus.RequiresRestart);
        Assert.DoesNotContain("installed successfully", Assert.Single(plugin.TextSettings).Description);
        Assert.Equal(0, host.CapabilityChangeCount); Assert.NotNull(GetPrivateField<WhisperFactory>(plugin, "_factory"));
    }

    [Fact]
    public async Task CancellationAfterCudaInstallationDoesNotLoseTheProcessRestartGate()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); using var cancellation = new CancellationTokenSource();
        var host = new FakePluginHostServices(temp.Path); host.SetSetting("acceleration", "NvidiaCuda");
        var installer = new FakeCudaRuntimeInstaller(temp.Path) { OnInstalled = cancellation.Cancel };
        using var plugin = new WhisperCppPlugin(installer); await plugin.ActivateAsync(host);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync("tiny", cancellation.Token));
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        using var reloaded = new WhisperCppPlugin(new FakeCudaRuntimeInstaller(temp.Path) { IsInstalledOverride = true });
        await reloaded.ActivateAsync(host);
        Assert.True(reloaded.AccelerationStatus.RequiresRestart);
        Assert.Contains("Restart TypeWhisper", Assert.Single(reloaded.TextSettings).Description);
    }

    [Fact]
    public async Task CudaRestartGateSurvivesFreshPluginInstancesInTheSameProcess()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var host = new FakePluginHostServices(temp.Path);
        host.SetSetting("acceleration", "NvidiaCuda");
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        using (var first = new WhisperCppPlugin(new FakeCudaRuntimeInstaller(temp.Path)))
        {
            await first.ActivateAsync(host);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.LoadModelAsync("tiny", default));
            await first.DeactivateAsync();
        }
        var installed = new FakeCudaRuntimeInstaller(temp.Path) { IsInstalledOverride = true };
        using var reloaded = new WhisperCppPlugin(installed); await reloaded.ActivateAsync(host);
        Assert.True(reloaded.AccelerationStatus.RequiresRestart);
        Assert.Contains("Restart TypeWhisper", Assert.Single(reloaded.TextSettings).Description);
        reloaded.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reloaded.LoadModelAsync("tiny", default));
        Assert.Contains("Restart TypeWhisper", error.Message); Assert.Equal(0, installed.EnsureInstalledCallCount);
        reloaded.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.False(reloaded.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task AutoCannotBypassTheNewlyInstalledCudaRestartGate()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var installer = new FakeCudaRuntimeInstaller(temp.Path);
        using var plugin = new WhisperCppPlugin(installer); await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync("tiny", default));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
        Assert.True(plugin.AccelerationStatus.RequiresRestart);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync("tiny", default));
        Assert.Contains("Restart TypeWhisper", error.Message); Assert.Equal(1, installer.EnsureInstalledCallCount);
        Assert.Null(GetPrivateField<WhisperFactory>(plugin, "_factory"));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Assert.False(plugin.AccelerationStatus.RequiresRestart);
    }

    [Fact]
    public async Task CanceledReplacementRetainsSelectionAndReloadsWithoutOverlappingFactories()
    {
        using var temp = new TempDirectory(); using var cancellation = new CancellationTokenSource();
        var activeFactories = 1;
        using var plugin = new WhisperCppPlugin
        {
            CreateFactory = _ =>
            {
                Assert.Equal(0, activeFactories); activeFactories++;
                cancellation.Cancel();
                return (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory));
            },
            ReleaseFactory = _ => activeFactories--
        };
        var host = new FakePluginHostServices(temp.Path); await plugin.ActivateAsync(host);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        plugin.SelectModel("base");
        SetPrivateField(plugin, "_factory", (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)));
        SetPrivateField(plugin, "_loadedModelId", "base");
        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-base.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync("tiny", cancellation.Token));
        Assert.Equal(0, activeFactories); Assert.Equal("base", plugin.SelectedModelId);
        Assert.Equal("base", host.GetSetting<string>("selectedModel")); Assert.True(plugin.IsConfigured);
        await plugin.LoadModelAsync(plugin.SelectedModelId!, default);
        Assert.Equal(1, activeFactories); Assert.Equal("base", GetPrivateField<string>(plugin, "_loadedModelId"));
    }

    [Fact]
    public async Task NativeFactoryConstructionDoesNotRunOnTheCallingThread()
    {
        using var temp = new TempDirectory();
        var callerThread = 0; var factoryThread = 0;
        using var plugin = new WhisperCppPlugin
        {
            CreateFactory = _ => { factoryThread = Environment.CurrentManagedThreadId; return (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)); },
            ReleaseFactory = _ => { }
        };
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Cpu);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try { callerThread = Environment.CurrentManagedThreadId; plugin.LoadModelAsync("tiny", default).GetAwaiter().GetResult(); complete.SetResult(); }
            catch (Exception ex) { complete.SetException(ex); }
        }) { IsBackground = true };
        caller.Start(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, factoryThread); Assert.NotEqual(callerThread, factoryThread);
    }

    [Fact]
    public async Task CancellationDuringRuntimePreparationPreservesExistingFactory()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); using var cancellation = new CancellationTokenSource();
        var installer = new FakeCudaRuntimeInstaller(temp.Path) { IsInstalledOverride = true, OnVerify = cancellation.Cancel };
        var released = 0;
        using var plugin = new WhisperCppPlugin(installer) { ReleaseFactory = _ => released++ };
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var previous = (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory));
        SetPrivateField(plugin, "_factory", previous); SetPrivateField(plugin, "_loadedModelId", "base");
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.LoadModelAsync("tiny", cancellation.Token));
        Assert.Same(previous, GetPrivateField<WhisperFactory>(plugin, "_factory")); Assert.Equal(0, released);
    }

    [Fact]
    public async Task SuccessfulCudaInstallationPublishesRestartGuidanceToPortableSettings()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) return;
        using var temp = new TempDirectory(); var host = new FakePluginHostServices(temp.Path);
        var installer = new FakeCudaRuntimeInstaller(temp.Path);
        using var plugin = new WhisperCppPlugin(installer); await plugin.ActivateAsync(host);
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.LoadModelAsync("tiny", default));
        var description = Assert.Single(plugin.TextSettings).Description;
        Assert.Contains("installed successfully", description); Assert.Contains("Restart TypeWhisper", description);
        Assert.Contains("select this model again", description); Assert.True(host.CapabilityChangeCount > 0);
    }

    [Theory]
    [InlineData(RuntimeLibrary.Cuda, TranscriptionAccelerationBackend.NvidiaCuda)]
    [InlineData(RuntimeLibrary.Vulkan, TranscriptionAccelerationBackend.AmdVulkan)]
    public void CompatiblePreferenceChangeKeepsLoadedBackendStatus(RuntimeLibrary library, TranscriptionAccelerationBackend backend)
    {
        var previous = RuntimeOptions.LoadedLibrary;
        try
        {
            RuntimeOptions.LoadedLibrary = library;
            using var plugin = new WhisperCppPlugin { ReleaseFactory = _ => { } };
            SetPrivateField(plugin, "_factory", (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)));
            plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
            Assert.Equal(backend, plugin.AccelerationStatus.ActiveBackend);
            Assert.Equal(backend, plugin.AccelerationDiagnostics.ActiveBackend);
            Assert.False(plugin.AccelerationStatus.RequiresRestart);
            plugin.SetAccelerationPreference(library == RuntimeLibrary.Cuda
                ? TranscriptionAccelerationPreference.NvidiaCuda : TranscriptionAccelerationPreference.AmdVulkan);
            Assert.Equal(backend, plugin.AccelerationStatus.ActiveBackend);
            Assert.False(plugin.AccelerationStatus.RequiresRestart);
        }
        finally { RuntimeOptions.LoadedLibrary = previous; }
    }

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
    [InlineData(0, 77691713)]
    [InlineData(60000000, 77691713)]
    [InlineData(77691712, 77691713)]
    public void IncompleteModelDownloadIsRejected(long bytes, long expected)
    {
        Assert.Throws<InvalidDataException>(() => WhisperCppPlugin.ValidateModelDownload(bytes, expected));
    }

    [Fact]
    public void TrustedModelSizeMustMatchExactly()
    {
        WhisperCppPlugin.ValidateModelDownload(77691713, 77691713);
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
        var runtimes = Path.Join(root, "plugins", "TypeWhisper.Plugin.WhisperCpp", "bin", configuration,
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
        Directory.CreateDirectory(Path.Join(temp.Path, "Models")); CreateModelFixture(Path.Join(temp.Path, "Models", "ggml-tiny.bin"));
        var before = installer.IntegrityReadCount;
        await plugin.LoadModelAsync("tiny", default);
        Assert.Equal(before + 1, installer.IntegrityReadCount);
    }
}
