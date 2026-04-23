using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class ModelManagerViewModelTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public ModelManagerViewModelTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
    }

    [Fact]
    public void Constructor_UsesSavedSelection_WhenNoModelIsActive()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: true));
        var modelManager = new ModelManagerService(pluginManager, settings);

        var sut = new ModelManagerViewModel(modelManager, settings);

        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Groq", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Large V3", sut.ActiveModelDisplayName);
    }

    [Fact]
    public async Task UnloadModel_KeepsSavedSelectionVisible()
    {
        const string pluginId = "com.typewhisper.groq";
        const string modelId = "whisper-large-v3";
        var fullModelId = ModelManagerService.GetPluginModelId(pluginId, modelId);
        var settings = new FakeSettingsService(new AppSettings
        {
            SelectedModelId = fullModelId
        });

        var pluginManager = CreatePluginManager(settings,
            new FakeTranscriptionPlugin(pluginId, "Groq", modelId, "Whisper Large V3", configured: true));
        var modelManager = new ModelManagerService(pluginManager, settings);
        var sut = new ModelManagerViewModel(modelManager, settings);

        await modelManager.LoadModelAsync(fullModelId);
        modelManager.UnloadModel();

        Assert.Equal(fullModelId, sut.SelectedModelOptionId);
        Assert.Equal("Groq", sut.ActiveProviderDisplayName);
        Assert.Equal("Whisper Large V3", sut.ActiveModelDisplayName);
    }

    private PluginManager CreatePluginManager(ISettingsService settings, params ITranscriptionEnginePlugin[] transcriptionEngines)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            settings,
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

    private sealed class FakeSettingsService(AppSettings initialSettings) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initialSettings;
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;

        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private sealed class FakeTranscriptionPlugin : ITranscriptionEnginePlugin
    {
        public FakeTranscriptionPlugin(
            string pluginId,
            string providerDisplayName,
            string modelId,
            string modelDisplayName,
            bool configured)
        {
            PluginId = pluginId;
            ProviderDisplayName = providerDisplayName;
            IsConfigured = configured;
            TranscriptionModels = [new PluginModelInfo(modelId, modelDisplayName)];
        }

        public string PluginId { get; }
        public string PluginName => PluginId;
        public string PluginVersion => "1.0.0";
        public string ProviderId => PluginId;
        public string ProviderDisplayName { get; }
        public bool IsConfigured { get; }
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string selectedModelId) => SelectedModelId = selectedModelId;

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
