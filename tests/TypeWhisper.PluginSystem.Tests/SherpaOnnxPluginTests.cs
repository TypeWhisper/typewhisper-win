using System.IO;
using System.Net.Http;
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"tw-sherpa-cuda-{Guid.NewGuid():N}");
        try
        {
            var nativeDir = Path.Combine(
                tempDir,
                "Runtimes",
                "sherpa-onnx-cuda",
                SherpaCudaRuntimeInstaller.RuntimeVersion,
                "native");
            Directory.CreateDirectory(nativeDir);
            File.WriteAllText(Path.Combine(nativeDir, "sherpa-onnx-c-api.dll"), "");
            File.WriteAllText(Path.Combine(nativeDir, "onnxruntime.dll"), "");
            File.WriteAllText(Path.Combine(nativeDir, "onnxruntime_providers_cuda.dll"), "");

            var installer = new SherpaCudaRuntimeInstaller(tempDir, new HttpClient());

            Assert.False(installer.IsInstalled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private sealed class FakeCudaRuntimeInstaller : ISherpaCudaRuntimeInstaller
    {
        public FakeCudaRuntimeInstaller(bool isInstalled)
        {
            IsInstalled = isInstalled;
        }

        public bool EnsureInstalledCalled { get; private set; }
        public bool IsInstalled { get; private set; }
        public string? RuntimeDirectory => IsInstalled ? Path.Join(Path.GetTempPath(), "sherpa-cuda-runtime") : null;

        public Task EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            EnsureInstalledCalled = true;
            IsInstalled = true;
            return Task.CompletedTask;
        }
    }
}
