using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.PluginSystem.Tests;

public class WhisperCppPluginTests
{
    [Fact]
    public void PluginVersion_MatchesManifestVersion()
    {
        var repoRoot = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var manifestPath = Path.Join(
            repoRoot,
            "plugins",
            "TypeWhisper.Plugin.WhisperCpp",
            "manifest.json");
        var manifest = JsonSerializer.Deserialize<PluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var sut = new WhisperCppPlugin();

        Assert.NotNull(manifest);
        Assert.Equal("1.0.2", manifest.Version);
        Assert.Equal(manifest.Version, sut.PluginVersion);
    }

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, RuntimeLibrary.Cuda, RuntimeLibrary.Cpu)]
    [InlineData(TranscriptionAccelerationPreference.Cpu, RuntimeLibrary.Cpu)]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, RuntimeLibrary.Cuda)]
    public void GetRuntimeLibraryOrder_MapsAccelerationPreference(
        TranscriptionAccelerationPreference preference,
        params RuntimeLibrary[] expectedOrder)
    {
        var order = WhisperCppPlugin.GetRuntimeLibraryOrder(preference);

        Assert.Equal(expectedOrder, order);
    }

    [Fact]
    public void CudaRuntimePackage_IsReferencedAndRequiredInPluginOutput()
    {
        var repoRoot = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var projectPath = Path.Join(
            repoRoot,
            "plugins",
            "TypeWhisper.Plugin.WhisperCpp",
            "TypeWhisper.Plugin.WhisperCpp.csproj");

        var project = File.ReadAllText(projectPath);

        Assert.Contains("Whisper.net.Runtime.Cuda.Windows", project);
        Assert.Contains("ggml-cuda-whisper.dll", project);
    }

    [Fact]
    public void PublishPluginsWorkflow_IncludesCudaRuntimeDirectoryInReleaseZip()
    {
        var repoRoot = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var workflowPath = Path.Join(
            repoRoot,
            ".github",
            "workflows",
            "publish-plugins.yml");

        var workflow = File.ReadAllText(workflowPath);

        Assert.Contains("cuda/win-x64", workflow);
        Assert.Contains("ggml-cuda-whisper.dll", workflow);
    }

    [Fact]
    public void CreateLoadedAccelerationStatus_ExplicitCudaCpuFallbackShowsUnavailableReason()
    {
        var method = typeof(WhisperCppPlugin).GetMethod(
            "CreateLoadedAccelerationStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var status = Assert.IsType<TranscriptionAccelerationStatus>(method.Invoke(
            null,
            [RuntimeLibrary.Cpu, TranscriptionAccelerationPreference.NvidiaCuda]));

        Assert.Equal(TranscriptionAccelerationBackend.Cpu, status.ActiveBackend);
        Assert.Equal("CUDA unavailable", status.DisplayText);
        Assert.Contains("could not be loaded", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cublas64_13.dll", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expected CPU native DLLs", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(status.Detail?.Length < 180, $"Detail was too long for compact UI: {status.Detail}");
    }

    [Fact]
    public void SetAccelerationPreference_ExplicitCudaWithoutRuntimeExplainsDownload()
    {
        var installer = new FakeCudaRuntimeInstaller(Path.Join(
            Path.GetTempPath(),
            "typewhisper-tests",
            "runtimes",
            "cuda",
            "win-x64"));
        var sut = new WhisperCppPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal("Using CPU", sut.AccelerationStatus.DisplayText);
        Assert.Contains("download", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CUDA", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetAccelerationPreference_ExplicitCudaWithRuntimeShowsCudaPending()
    {
        var installer = new FakeCudaRuntimeInstaller(Path.Join(
            Path.GetTempPath(),
            "typewhisper-tests",
            "runtimes",
            "cuda",
            "win-x64"))
        {
            IsInstalledOverride = true
        };
        var sut = new WhisperCppPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal("Using CPU", sut.AccelerationStatus.DisplayText);
        Assert.DoesNotContain("download", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateNativeLoadFailureStatus_ExplicitCudaUsesCompactDependencyHint()
    {
        var method = typeof(WhisperCppPlugin).GetMethod(
            "CreateNativeLoadFailureStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var nativeError = new InvalidOperationException(
            "Unable to load the whisper.cpp native runtime. Expected CPU native DLLs under " +
            @"'C:\Users\Sal\AppData\Local\TypeWhisper\Plugins\com.typewhisper.whisper-cpp\runtimes\win-x64' " +
            "and CUDA native DLLs under " +
            @"'C:\Users\Sal\AppData\Local\TypeWhisper\Plugins\com.typewhisper.whisper-cpp\runtimes\cuda\win-x64', " +
            "including cublas64_13.dll. Original error: 0x8007007E.");

        Assert.NotNull(method);

        var status = Assert.IsType<TranscriptionAccelerationStatus>(method.Invoke(
            null,
            [nativeError, TranscriptionAccelerationPreference.NvidiaCuda]));

        Assert.Equal("CUDA unavailable", status.DisplayText);
        Assert.Contains("cublas64_13.dll", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Sal", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Expected CPU native DLLs", status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(status.Detail?.Length < 180, $"Detail was too long for compact UI: {status.Detail}");
    }

    [Fact]
    public void BuildNativeLoadFailureMessage_NamesRuntimeFolderAndVcRedistFallback()
    {
        var pluginDirectory = Path.Join(
            @"C:\", "Users", "Sal", "AppData", "Local", "TypeWhisper", "Plugins", "com.typewhisper.whisper-cpp");
        var nativeError = new DllNotFoundException(
            "Unable to load DLL 'ggml-cpu-whisper.dll' or one of its dependencies: The specified module could not be found. (0x8007007E)");

        var message = WhisperCppPlugin.BuildNativeLoadFailureMessage(
            pluginDirectory,
            "win-x64",
            nativeError);

        Assert.Contains(Path.Join(pluginDirectory, "runtimes", "win-x64"), message);
        Assert.Contains("ggml-cpu-whisper.dll", message);
        Assert.Contains("VCOMP140.DLL", message);
        Assert.Contains("cublas64_13.dll", message);
        Assert.Contains("Microsoft Visual C++ 2015-2022 Redistributable", message);
        Assert.Contains("0x8007007E", message);
    }

    [Fact]
    public void BuildNativeLoadFailureMessage_StripsPathLikeRuntimeIdentifier()
    {
        var pluginDirectory = Path.Join(
            @"C:\", "Users", "Sal", "AppData", "Local", "TypeWhisper", "Plugins", "com.typewhisper.whisper-cpp");
        var nativeError = new DllNotFoundException("Unable to load DLL 'ggml-cpu-whisper.dll'.");

        var message = WhisperCppPlugin.BuildNativeLoadFailureMessage(
            pluginDirectory,
            Path.Join("unexpected", "win-x64"),
            nativeError);

        Assert.Contains(Path.Join(pluginDirectory, "runtimes", "win-x64"), message);
        Assert.DoesNotContain(Path.Join(pluginDirectory, "runtimes", "unexpected"), message);
    }

    [Fact]
    public async Task CudaRuntimeInstaller_DownloadsAndInstallsCublasRuntimeBesideCudaRuntime()
    {
        using var temp = new TempDirectory();
        var runtimeDirectory = Path.Join(temp.Path, "runtimes", "cuda", "win-x64");
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllText(Path.Join(runtimeDirectory, "ggml-cuda-whisper.dll"), "native");

        var archiveBytes = CreateZipArchive(
            ("libcublas/bin/cublas64_13.dll", "cublas"),
            ("libcublas/bin/cublasLt64_13.dll", "cublasLt"),
            ("libcublas/docs/readme.txt", "ignore"));
        var package = new WhisperCppCudaRuntimePackage(
            "test-runtime",
            "https://example.test/libcublas.zip",
            Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant(),
            ["cublas64_13.dll", "cublasLt64_13.dll"]);
        using var httpClient = new HttpClient(new StaticArchiveHandler(archiveBytes));
        var sut = new WhisperCppCudaRuntimeInstaller(temp.Path, httpClient, package);

        Assert.False(sut.IsInstalled);

        await sut.EnsureInstalledAsync(CancellationToken.None);

        Assert.True(sut.IsInstalled);
        Assert.Equal("cublas", File.ReadAllText(Path.Join(runtimeDirectory, "cublas64_13.dll")));
        Assert.Equal("cublasLt", File.ReadAllText(Path.Join(runtimeDirectory, "cublasLt64_13.dll")));
        Assert.False(File.Exists(Path.Join(runtimeDirectory, "readme.txt")));
    }

    [Fact]
    public async Task LoadModelAsync_ExplicitCudaInstallsMissingRuntimeBeforeNativeLoad()
    {
        using var temp = new TempDirectory();
        var host = new FakePluginHostServices(temp.Path);
        var installer = new FakeCudaRuntimeInstaller(
            Path.Join(temp.Path, "plugin", "runtimes", "cuda", "win-x64"))
        {
            InstallException = new OperationCanceledException("cancelled before native load")
        };
        var sut = new WhisperCppPlugin(installer);
        await sut.ActivateAsync(host);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        await File.WriteAllTextAsync(Path.Join(temp.Path, "Models", "ggml-tiny.bin"), "not a real model");

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.LoadModelAsync("tiny", CancellationToken.None));

        Assert.Equal(1, installer.EnsureInstalledCallCount);
    }

    [Fact]
    public async Task LoadModelAsync_ExplicitCudaRuntimeInstallFailureShowsDownloadReason()
    {
        using var temp = new TempDirectory();
        var host = new FakePluginHostServices(temp.Path);
        var installer = new FakeCudaRuntimeInstaller(
            Path.Join(temp.Path, "plugin", "runtimes", "cuda", "win-x64"))
        {
            InstallException = new InvalidOperationException("network offline")
        };
        var sut = new WhisperCppPlugin(installer);
        await sut.ActivateAsync(host);
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Directory.CreateDirectory(Path.Join(temp.Path, "Models"));
        await File.WriteAllTextAsync(Path.Join(temp.Path, "Models", "ggml-tiny.bin"), "not a real model");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LoadModelAsync("tiny", CancellationToken.None));

        Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
        Assert.Contains("download", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network offline", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("network offline", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_DisposesCudaRuntimeInstaller()
    {
        var installer = new FakeCudaRuntimeInstaller(Path.Join(
            Path.GetTempPath(),
            "typewhisper-tests",
            "runtimes",
            "cuda",
            "win-x64"));
        var sut = new WhisperCppPlugin(installer);

        sut.Dispose();

        Assert.True(installer.DisposeCalled);
    }

    private static byte[] CreateZipArchive(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return output.ToArray();
    }

    private sealed class StaticArchiveHandler(byte[] archiveBytes) : HttpMessageHandler
    {
        [SuppressMessage(
            "Reliability",
            "CA2000:Dispose objects before losing scope",
            Justification = "The response is returned to HttpClient, which disposes it after the request pipeline completes.")]
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FakeCudaRuntimeInstaller(string runtimeDirectory) : IWhisperCppCudaRuntimeInstaller, IDisposable
    {
        private bool _isInstalled;
        public int EnsureInstalledCallCount { get; private set; }
        public bool DisposeCalled { get; private set; }
        public Exception? InstallException { get; init; }
        public bool IsInstalledOverride { get; init; }
        public bool IsInstalled => _isInstalled || IsInstalledOverride;
        public string RuntimeDirectory { get; } = runtimeDirectory;

        public Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            EnsureInstalledCallCount++;
            if (InstallException is not null)
                throw InstallException;

            _isInstalled = true;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCalled = true;
    }

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

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Join(
            System.IO.Path.GetTempPath(),
            $"typewhisper-tests-{Guid.NewGuid():N}");

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Failed to delete temporary test directory '{Path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Failed to delete temporary test directory '{Path}': {ex.Message}");
            }
        }
    }
}
