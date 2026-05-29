using System.IO;
using System.Net.Http;
using TypeWhisper.PluginSDK;
using TypeWhisper.Plugin.SherpaOnnx;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class SherpaOnnxPluginTests
{
    [Theory]
    [InlineData(TranscriptionAccelerationPreference.Auto, false, "cpu")]
    [InlineData(TranscriptionAccelerationPreference.Auto, true, "cuda")]
    [InlineData(TranscriptionAccelerationPreference.Cpu, false, "cpu")]
    [InlineData(TranscriptionAccelerationPreference.NvidiaCuda, false, "cuda")]
    public void GetProvider_MapsAccelerationPreference(
        TranscriptionAccelerationPreference preference,
        bool cudaRuntimeInstalled,
        string expectedProvider)
    {
        var provider = SherpaOnnxPlugin.GetProvider(preference, cudaRuntimeInstalled);

        Assert.Equal(expectedProvider, provider);
    }

    [Fact]
    public async Task ResolveProviderForLoadAsync_AutoUsesCpuWithoutInstallingCudaRuntime()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: false);
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);

        var provider = await sut.ResolveProviderForLoadAsync(CancellationToken.None);

        Assert.Equal("cpu", provider);
        Assert.False(installer.EnsureInstalledCalled);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Contains("CUDA runtime is not installed", sut.AccelerationStatus.Detail);
    }

    [Fact]
    public async Task ResolveProviderForLoadAsync_ExplicitCudaInstallsRuntimeAndUsesCuda()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: false);
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var provider = await sut.ResolveProviderForLoadAsync(CancellationToken.None);

        Assert.Equal("cuda", provider);
        Assert.True(installer.EnsureInstalledCalled);
        Assert.Equal(TranscriptionAccelerationBackend.NvidiaCuda, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("Using CUDA", sut.AccelerationStatus.DisplayText);
    }

    [Fact]
    public async Task ResolveProviderForLoadAsync_ExplicitCudaInstallFailureSetsUnavailableStatus()
    {
        var installer = new FakeCudaRuntimeInstaller(
            isInstalled: false,
            installException: new InvalidOperationException("download blocked"));
        var sut = new SherpaOnnxPlugin(installer);

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveProviderForLoadAsync(CancellationToken.None));

        Assert.Contains("download blocked", ex.Message);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
        Assert.Contains("download blocked", sut.AccelerationStatus.Detail);
    }

    [Fact]
    public async Task LoadModelAsync_ExplicitCudaProviderFailureSetsUnavailableStatus()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-load-{Guid.NewGuid():N}");
        try
        {
            var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
            var sut = new SherpaOnnxPlugin(
                installer,
                (_, _, _) => throw new InvalidOperationException("CUDA provider failed to initialize."));
            var host = new FakePluginHostServices(tempDir);
            CreateParakeetModelFiles(tempDir);

            await sut.ActivateAsync(host);
            sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => sut.LoadModelAsync("parakeet-tdt-0.6b", CancellationToken.None));

            Assert.Contains("CUDA provider failed to initialize", ex.Message);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
            Assert.Equal("CUDA unavailable", sut.AccelerationStatus.DisplayText);
            Assert.Contains("CUDA provider failed to initialize", sut.AccelerationStatus.Detail);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolveProviderForLoadAsync_BackendSwitchAfterNativeLoadRequiresRestart()
    {
        var installer = new FakeCudaRuntimeInstaller(isInstalled: true);
        var sut = new SherpaOnnxPlugin(installer);

        sut.MarkNativeRuntimeLoadedForTests("cpu");
        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResolveProviderForLoadAsync(CancellationToken.None));

        Assert.Contains("restart", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(sut.AccelerationStatus.RequiresRestart);
        Assert.Contains("restart", sut.AccelerationStatus.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("cuda")]
    public void CreateRecognizerConfigs_UseMappedProvider(string provider)
    {
        var modelDir = Path.Join(Path.GetTempPath(), "tw-sherpa-test");

        var parakeet = SherpaOnnxPlugin.CreateParakeetConfig(modelDir, provider);
        var canary = SherpaOnnxPlugin.CreateCanaryConfig(modelDir, "en", "en", provider);

        Assert.Equal(provider, parakeet.ModelConfig.Provider);
        Assert.Equal(provider, canary.ModelConfig.Provider);
    }

    [Fact]
    public void CudaRuntimeInstaller_IsNotInstalledWhenOnnxCudaDependenciesAreMissing()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var nativeDir = Path.Join(
                tempDir,
                "Runtimes",
                "sherpa-onnx-cuda",
                SherpaCudaRuntimeInstaller.RuntimeVersion,
                "native");
            Directory.CreateDirectory(nativeDir);
            File.WriteAllText(Path.Join(nativeDir, "sherpa-onnx-c-api.dll"), "");
            File.WriteAllText(Path.Join(nativeDir, "onnxruntime.dll"), "");
            File.WriteAllText(Path.Join(nativeDir, "onnxruntime_providers_cuda.dll"), "");

            var installer = new SherpaCudaRuntimeInstaller(tempDir, new HttpClient());

            Assert.False(installer.IsInstalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void CreateParakeetModelFiles(string pluginDataDirectory)
    {
        var modelDir = Path.Join(pluginDataDirectory, "Models", "parakeet-tdt-0.6b");
        Directory.CreateDirectory(modelDir);
        foreach (var fileName in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
            File.WriteAllText(Path.Join(modelDir, fileName), "test");
    }

    private sealed class FakeCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
    {
        private readonly Exception? _installException;

        public FakeCudaRuntimeInstaller(bool isInstalled, Exception? installException = null)
        {
            IsInstalled = isInstalled;
            _installException = installException;
        }

        public bool EnsureInstalledCalled { get; private set; }
        public bool IsInstalled { get; private set; }
        public string? RuntimeDirectory => IsInstalled ? Path.Join(Path.GetTempPath(), "sherpa-cuda-runtime") : null;

        public Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            EnsureInstalledCalled = true;
            if (_installException is not null)
                throw _installException;

            IsInstalled = true;
            return Task.CompletedTask;
        }
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
}
