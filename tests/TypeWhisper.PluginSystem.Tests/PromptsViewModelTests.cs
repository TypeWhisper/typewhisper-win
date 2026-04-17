using System.IO;
using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class PromptsViewModelTests
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();

    public PromptsViewModelTests()
    {
        _profiles.Setup(p => p.Profiles).Returns([]);
    }

    [Fact]
    public void SelectedDefaultProvider_FallsBackToVisibleDefaultOption()
    {
        var settings = new FakeSettingsService(new AppSettings());
        var promptActions = CreatePromptActionService();
        var provider = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var pluginManager = CreatePluginManager(settings, provider);

        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        Assert.Null(settings.Current.DefaultLlmProvider);
        Assert.NotNull(sut.SelectedDefaultProvider);
        Assert.Equal(sut.AvailableProviders.First(), sut.SelectedDefaultProvider);
        Assert.Null(sut.SelectedDefaultProvider!.Value);
    }

    [Fact]
    public void SelectedDefaultProvider_PersistsExplicitProviderChoice()
    {
        var settings = new FakeSettingsService(new AppSettings());
        var promptActions = CreatePromptActionService();
        var provider = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var pluginManager = CreatePluginManager(settings, provider);
        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        var explicitOption = sut.AvailableProviders.Single(option =>
            option.Value == "plugin:com.typewhisper.groq:llama-3.3-70b-versatile");

        sut.SelectedDefaultProvider = explicitOption;

        Assert.Equal(explicitOption.Value, settings.Current.DefaultLlmProvider);
        Assert.Equal(explicitOption, sut.SelectedDefaultProvider);
    }

    private Mock<IPromptActionService> CreatePromptActionService()
    {
        var promptActions = new Mock<IPromptActionService>();
        promptActions.Setup(service => service.Actions).Returns([]);
        promptActions.Setup(service => service.EnabledActions).Returns([]);
        return promptActions;
    }

    private PluginManager CreatePluginManager(ISettingsService settings, Mock<ILlmProviderPlugin> provider)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            settings,
            []);

        var manifest = new PluginManifest
        {
            Id = "com.typewhisper.groq",
            Name = "Groq",
            Version = "1.0.0",
            AssemblyName = Path.GetFileName(Assembly.GetExecutingAssembly().Location),
            PluginClass = typeof(object).FullName ?? "System.Object"
        };

        var loadedPlugin = new LoadedPlugin(
            manifest,
            provider.Object,
            new PluginAssemblyLoadContext(Assembly.GetExecutingAssembly().Location),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory);

        SetPrivateField(pluginManager, "_allPlugins", new List<LoadedPlugin> { loadedPlugin });
        SetPrivateField(pluginManager, "_llmProviders", new List<ILlmProviderPlugin> { provider.Object });
        return pluginManager;
    }

    private static Mock<ILlmProviderPlugin> CreateLlmProvider(
        string pluginId,
        string providerName,
        string modelId,
        string modelDisplayName)
    {
        var provider = new Mock<ILlmProviderPlugin>();
        provider.SetupGet(plugin => plugin.PluginId).Returns(pluginId);
        provider.SetupGet(plugin => plugin.PluginName).Returns(providerName);
        provider.SetupGet(plugin => plugin.PluginVersion).Returns("1.0.0");
        provider.SetupGet(plugin => plugin.ProviderName).Returns(providerName);
        provider.SetupGet(plugin => plugin.IsAvailable).Returns(true);
        provider.SetupGet(plugin => plugin.SupportedModels).Returns([new PluginModelInfo(modelId, modelDisplayName)]);
        return provider;
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
}
