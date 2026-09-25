using System.Runtime.CompilerServices;
using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Theory]
    [InlineData(new[] { true, false }, 1)]
    [InlineData(new[] { false, true }, 0)]
    [InlineData(new[] { true, true }, 0)]
    [InlineData(new[] { true }, 0)]
    public void VulkanPrefersTheFirstDedicatedGpu(bool[] integrated, int expected) =>
        Assert.Equal(expected, GpuDevices.Select(integrated.Select((value, index) => new GpuDevice($"GPU {index}", value)).ToArray()));

    [Fact]
    public void GpuListIsUnavailableWithoutTheRuntime()
    {
        using var temp = new TempDirectory();
        Assert.Null(GpuDevices.List(temp.Path));
    }

    [Theory]
    [InlineData(TranscriptionAccelerationPreference.AmdVulkan, "Vulkan unavailable")]
    [InlineData(TranscriptionAccelerationPreference.Auto, "Using CPU")]
    public void VulkanRuntimeWithoutGpuReportsCpu(TranscriptionAccelerationPreference preference, string displayText)
    {
        var status = WhisperCppPlugin.CreateLoadedAccelerationStatus(RuntimeLibrary.Vulkan, preference, vulkanHasNoGpu: true);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, status.ActiveBackend);
        Assert.Equal(displayText, status.DisplayText);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan,
            WhisperCppPlugin.CreateLoadedAccelerationStatus(RuntimeLibrary.Vulkan, preference).ActiveBackend);
    }

    [Fact]
    public async Task VulkanRuntimeWithoutGpuStaysOnCpuUntilRestart()
    {
        var previous = RuntimeOptions.LoadedLibrary;
        try
        {
            RuntimeOptions.LoadedLibrary = RuntimeLibrary.Vulkan;
            using var temp = new TempDirectory();
            using var plugin = new WhisperCppPlugin { ReleaseFactory = _ => { } };
            await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
            SetPrivateField(plugin, "_factory", (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)));
            SetPrivateField(plugin, "_vulkanHasNoGpu", true);
            plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.Auto);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
            Assert.EndsWith("In use: CPU", Assert.Single(plugin.TextSettings).Description);
            plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
            Assert.True(plugin.AccelerationStatus.RequiresRestart);
            Assert.Equal(TranscriptionAccelerationBackend.Cpu, plugin.AccelerationStatus.ActiveBackend);
            Assert.EndsWith("In use: CPU", Assert.Single(plugin.TextSettings).Description);
            SetPrivateField<WhisperFactory?>(plugin, "_factory", null);
        }
        finally { RuntimeOptions.LoadedLibrary = previous; }
    }

    // Opt-in: set TYPEWHISPER_TEST_WHISPER_DATA to a whisper.cpp plugin data folder containing Models/ggml-large-v3-turbo.bin.
    [Fact]
    public async Task VulkanModelLoadsOnTheSelectedGpu()
    {
        var data = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_WHISPER_DATA");
        if (string.IsNullOrWhiteSpace(data) || !OperatingSystem.IsWindows()) return;
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(data));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        plugin.SelectModel("large-v3-turbo");
        await plugin.LoadModelAsync("large-v3-turbo", default);
        var gpu = GetPrivateField<GpuDevice>(plugin, "_gpuDevice");
        Assert.NotNull(gpu);
        Assert.Equal(TranscriptionAccelerationBackend.AmdVulkan, plugin.AccelerationStatus.ActiveBackend);
        Assert.Contains(gpu.Name, plugin.AccelerationStatus.Detail);
        var result = await plugin.TranscribePcmAsync(new float[16000 * 2], "en", false, default);
        Console.WriteLine($"GPU: {gpu.Name} integrated={gpu.Integrated}; {Assert.Single(plugin.TextSettings).Description}; text='{result.Text}'");
    }

    [Fact]
    public async Task ProcessingDeviceNamesTheGpuTheModelRunsOn()
    {
        using var temp = new TempDirectory();
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path));
        plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.AmdVulkan);
        Assert.DoesNotContain("In use:", Assert.Single(plugin.TextSettings).Description);
        SetPrivateField(plugin, "_factory", (WhisperFactory)RuntimeHelpers.GetUninitializedObject(typeof(WhisperFactory)));
        SetPrivateField<GpuDevice?>(plugin, "_gpuDevice", new("AMD Radeon(TM) Graphics", Integrated: true));
        SetPrivateField(plugin, "_accelerationStatus", new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.AmdVulkan, "Using Vulkan"));
        Assert.EndsWith("In use: Vulkan · AMD Radeon(TM) Graphics (integrated graphics)", Assert.Single(plugin.TextSettings).Description);
        SetPrivateField(plugin, "_accelerationStatus", new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.Cpu, "Vulkan unavailable"));
        Assert.EndsWith("In use: CPU (Vulkan unavailable)", Assert.Single(plugin.TextSettings).Description);
        SetPrivateField<WhisperFactory?>(plugin, "_factory", null);
    }
}
