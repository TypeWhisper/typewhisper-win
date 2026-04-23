using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerServiceTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public ModelManagerServiceTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
    }

    [Fact]
    public void Engine_WithoutActiveModel_DoesNotFallbackToArbitraryConfiguredPlugin()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = ModelManagerService.GetPluginModelId("com.typewhisper.sherpa-onnx", "parakeet")
        });

        var pluginManager = CreatePluginManager(
            new FakeTranscriptionPlugin("com.typewhisper.openai-compatible", configured: true, selectedModelId: "whisper"),
            new FakeTranscriptionPlugin("com.typewhisper.sherpa-onnx", configured: true, selectedModelId: null));

        var sut = new ModelManagerService(pluginManager, _settings.Object);

        Assert.IsType<NoOpTranscriptionEngine>(sut.Engine);
        Assert.False(sut.Engine.IsModelLoaded);
    }

    [Fact]
    public async Task EnsureModelLoadedAsync_LoadsSelectedModel_WhenNoActiveModelExists()
    {
        const string pluginId = "com.typewhisper.sherpa-onnx";
        const string modelId = "parakeet";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);

        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var plugin = new FakeTranscriptionPlugin(
            pluginId,
            configured: true,
            selectedModelId: null,
            supportsModelDownload: true);
        var pluginManager = CreatePluginManager(plugin);
        var sut = new ModelManagerService(pluginManager, _settings.Object);

        var loaded = await sut.EnsureModelLoadedAsync();

        Assert.True(loaded);
        Assert.Equal(fullModelId, sut.ActiveModelId);
        Assert.Equal(modelId, plugin.SelectedModelId);
        Assert.Equal(modelId, plugin.LastLoadedModelId);
        Assert.True(sut.Engine.IsModelLoaded);
    }

    private PluginManager CreatePluginManager(params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object,
            []);

        SetPrivateField(pluginManager, "_transcriptionEngines", transcriptionEngines.ToList());
        return pluginManager;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            bool configured,
            string? selectedModelId,
            bool supportsModelDownload = false)
        {
            PluginId = pluginId;
            IsConfigured = configured;
            SelectedModelId = selectedModelId;
            SupportsModelDownload = supportsModelDownload;
            TranscriptionModels = [new PluginModelInfo("parakeet", "Parakeet"), new PluginModelInfo("whisper", "Whisper")];
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName => PluginId;
        public bool IsConfigured { get; }
        public bool SupportsModelDownload { get; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public string? LastLoadedModelId { get; private set; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public Task LoadModelAsync(string modelId, CancellationToken ct)
        {
            LastLoadedModelId = modelId;
            SelectedModelId = modelId;
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(byte[] wavAudio, string? language, bool translate, string? prompt, CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("ok", language ?? "en", 1));

        public void Dispose() { }
    }
}
