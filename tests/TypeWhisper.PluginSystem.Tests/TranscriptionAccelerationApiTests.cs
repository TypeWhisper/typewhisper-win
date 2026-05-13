using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.PluginSystem.Tests;

public class TranscriptionAccelerationApiTests
{
    [Fact]
    public void DefaultPluginAccelerationCapabilities_AreCpuOnlyAndAutoPreferred()
    {
        ITranscriptionEnginePlugin sut = new MinimalTranscriptionPlugin();

        Assert.Equal(TranscriptionAccelerationPreference.Auto, sut.AccelerationPreference);
        Assert.Equal([TranscriptionAccelerationBackend.Cpu], sut.SupportedAccelerationBackends);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
        Assert.Contains("CPU", sut.AccelerationStatus.DisplayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultSetAccelerationPreference_DoesNotBreakLegacyPlugins()
    {
        ITranscriptionEnginePlugin sut = new MinimalTranscriptionPlugin();

        sut.SetAccelerationPreference(TranscriptionAccelerationPreference.NvidiaCuda);

        Assert.Equal(TranscriptionAccelerationPreference.Auto, sut.AccelerationPreference);
        Assert.Equal(TranscriptionAccelerationBackend.Cpu, sut.AccelerationStatus.ActiveBackend);
    }

    private sealed class MinimalTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public string PluginId => "com.test.minimal";
        public string PluginName => "Minimal";
        public string PluginVersion => "1.0.0";
        public string ProviderId => "minimal";
        public string ProviderDisplayName => "Minimal";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new("tiny", "Tiny")];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }
}
