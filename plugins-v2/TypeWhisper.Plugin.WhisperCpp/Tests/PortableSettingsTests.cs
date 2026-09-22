using TypeWhisper.Plugin.WhisperCpp;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public partial class WhisperCppPluginTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstDecodeRestoresSelectedModelAfterActivation(bool pcm)
    {
        using var temp = new TempDirectory();
        var host = new FakePluginHostServices(temp.Path);
        host.SetSetting("selectedModel", "large-v3-turbo");
        using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(host);
        // A missing weight file must reach the loader, not fail with "No model loaded".
        var error = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() => pcm
            ? plugin.TranscribePcmAsync(new float[16000], "de", false, default)
            : plugin.TranscribeAsync(new byte[44], "de", false, null, default));
        Assert.Contains("large-v3-turbo", error.Message);
        Assert.Equal("large-v3-turbo", plugin.SelectedModelId);
    }

    [Fact]
    public async Task SharedSettingsPersistProcessingDeviceAcrossActivation()
    {
        using var temp = new TempDirectory(); var host = new FakePluginHostServices(temp.Path);
        using var plugin = new WhisperCppPlugin(); await plugin.ActivateAsync(host);
        Assert.False(Assert.Single(plugin.TextSettings).SaveChoiceOnChange);
        await plugin.SaveTextSettingAsync("acceleration", "NvidiaCuda", default);
        Assert.Equal(TranscriptionAccelerationPreference.NvidiaCuda, plugin.AccelerationPreference);
        await plugin.DeactivateAsync(); await plugin.ActivateAsync(host);
        Assert.Equal("NvidiaCuda", Assert.Single(plugin.TextSettings).Value);
    }

    [Fact]
    public async Task FailedOrInvalidSettingsKeepActiveDevice()
    {
        using var temp = new TempDirectory(); var host = new FakePluginHostServices(temp.Path);
        using var plugin = new WhisperCppPlugin(); await plugin.ActivateAsync(host);
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.SaveTextSettingAsync("acceleration", "999", default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.SaveTextSettingAsync("acceleration", "Cpu", new(true)));
        host.FailSetting = true;
        await Assert.ThrowsAsync<IOException>(() => plugin.SaveTextSettingAsync("acceleration", "NvidiaCuda", default));
        Assert.Equal(TranscriptionAccelerationPreference.Auto, plugin.AccelerationPreference);
    }

    [Fact]
    public void LanguageChoicesMatchEnglishOnlyAndMultilingualModels()
    {
        using var plugin = new WhisperCppPlugin(); plugin.SelectModel("base.en");
        Assert.Equal(new[] { "en" }, plugin.SupportedLanguages);
        plugin.SelectModel("large-v3-turbo"); Assert.Contains("de", plugin.SupportedLanguages); Assert.Contains("en", plugin.SupportedLanguages);
        Assert.True(plugin.SupportsLocalLivePreview); Assert.IsAssignableFrom<IPcmTranscriptionEnginePlugin>(plugin);
        Assert.False(((ITranscriptionEnginePlugin)plugin).SupportsStreaming);
    }

    [Fact]
    public async Task SavingUnchangedDevicePreservesLoadedRuntimeStatus()
    {
        using var temp = new TempDirectory(); using var plugin = new WhisperCppPlugin();
        await plugin.ActivateAsync(new FakePluginHostServices(temp.Path)); plugin.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);
        var status = new TranscriptionAccelerationStatus(TranscriptionAccelerationBackend.NvidiaCuda, "Using CUDA");
        typeof(WhisperCppPlugin).GetField("_accelerationStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(plugin, status);
        await plugin.SaveTextSettingAsync("acceleration", "NvidiaCuda", default);
        Assert.Equal(status, plugin.AccelerationStatus);
    }

    [Theory]
    [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)]
    public async Task InvalidPcmIsRejectedBeforeNativeLoading(float value)
    {
        using var plugin = new WhisperCppPlugin();
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.TranscribePcmAsync(new[] { value }, "de", false, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plugin.TranscribePcmAsync(new float[16000], "de", false, new(true)));
        Assert.Empty((await plugin.TranscribePcmAsync(ReadOnlyMemory<float>.Empty, "de", false, default)).Text);
    }
}

