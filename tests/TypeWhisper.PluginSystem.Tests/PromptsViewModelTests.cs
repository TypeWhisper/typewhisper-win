using System.IO;
using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Localization;
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
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
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

    [Fact]
    public void DefaultProviderLabel_UsesConfiguredDefaultLlmProvider()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        });
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var openAi = CreateLlmProvider("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini", "GPT-4.1 Mini");
        var pluginManager = CreatePluginManager(settings, groq, openAi);

        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        Assert.Equal("(Default - OpenAI / GPT-4.1 Mini)", sut.AvailableProviders.First().DisplayName);
        Assert.Equal("(Default - OpenAI / GPT-4.1 Mini)", sut.DefaultProviderSummary);
    }

    [Fact]
    public void SettingsChanged_RebuildsAvailableProvidersAndUpdatesDefaultLabel()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.typewhisper.groq:llama-3.3-70b-versatile"
        });
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var openAi = CreateLlmProvider("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini", "GPT-4.1 Mini");
        var pluginManager = CreatePluginManager(settings, groq, openAi);
        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        settings.Save(settings.Current with
        {
            DefaultLlmProvider = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        });

        Assert.Equal("(Default - OpenAI / GPT-4.1 Mini)", sut.AvailableProviders.First().DisplayName);
        Assert.Equal("(Default - OpenAI / GPT-4.1 Mini)", sut.DefaultProviderSummary);
    }

    [Fact]
    public void SelectedDefaultProvider_ReflectsConfiguredDefaultAfterSettingsChange()
    {
        var settings = new FakeSettingsService(new AppSettings());
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var openAi = CreateLlmProvider("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini", "GPT-4.1 Mini");
        var pluginManager = CreatePluginManager(settings, groq, openAi);
        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        settings.Save(settings.Current with
        {
            DefaultLlmProvider = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        });

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.SelectedDefaultProvider?.Value);
        Assert.Equal("OpenAI / GPT-4.1 Mini", sut.SelectedDefaultProvider?.DisplayName);
    }

    [Fact]
    public void PromptEditorStandardSelection_RemainsNullOverride()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.typewhisper.groq:llama-3.3-70b-versatile"
        });
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var pluginManager = CreatePluginManager(settings, groq);
        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);
        var action = new PromptAction
        {
            Id = "prompt-1",
            Name = "Translate",
            SystemPrompt = "Translate this.",
            ProviderOverride = null
        };

        InvokePrivateMethod(sut, "PopulateEditorFromAction", action);

        Assert.Null(sut.EditProviderOverride);
        Assert.Null(sut.AvailableProviders.First().Value);
        Assert.Equal("(Default - Groq / Llama 3.3 70B Versatile)", sut.AvailableProviders.First().DisplayName);
        Assert.Equal(sut.AvailableProviders.First(), sut.SelectedEditProvider);
    }

    [Fact]
    public void SelectedEditProvider_UsesExplicitOverride_WhenPresent()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.typewhisper.groq:llama-3.3-70b-versatile"
        });
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var openAi = CreateLlmProvider("com.typewhisper.openai", "OpenAI", "gpt-4.1-mini", "GPT-4.1 Mini");
        var pluginManager = CreatePluginManager(settings, groq, openAi);
        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);
        var action = new PromptAction
        {
            Id = "prompt-2",
            Name = "Reply",
            SystemPrompt = "Write a reply.",
            ProviderOverride = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        };

        InvokePrivateMethod(sut, "PopulateEditorFromAction", action);

        Assert.Equal("plugin:com.typewhisper.openai:gpt-4.1-mini", sut.EditProviderOverride);
        Assert.Equal("OpenAI / GPT-4.1 Mini", sut.SelectedEditProvider?.DisplayName);
    }

    [Fact]
    public void DefaultProviderLabel_ShowsUnavailableState_WhenConfiguredDefaultCannotBeResolved()
    {
        var settings = new FakeSettingsService(new AppSettings
        {
            DefaultLlmProvider = "plugin:com.typewhisper.openai:gpt-4.1-mini"
        });
        var promptActions = CreatePromptActionService();
        var groq = CreateLlmProvider("com.typewhisper.groq", "Groq", "llama-3.3-70b-versatile", "Llama 3.3 70B Versatile");
        var pluginManager = CreatePluginManager(settings, groq);

        var sut = new PromptsViewModel(promptActions.Object, pluginManager, settings);

        Assert.Equal("(Default - none configured)", sut.AvailableProviders.First().DisplayName);
        Assert.Equal("(Default - none configured)", sut.DefaultProviderSummary);
        Assert.DoesNotContain("Groq", sut.AvailableProviders.First().DisplayName);
    }

    private Mock<IPromptActionService> CreatePromptActionService()
    {
        var promptActions = new Mock<IPromptActionService>();
        promptActions.Setup(service => service.Actions).Returns([]);
        promptActions.Setup(service => service.EnabledActions).Returns([]);
        return promptActions;
    }

    private PluginManager CreatePluginManager(ISettingsService settings, params Mock<ILlmProviderPlugin>[] providers)
    {
        var pluginManager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            settings,
            []);

        var loadedPlugins = providers
            .Select(provider => new LoadedPlugin(
                new PluginManifest
                {
                    Id = provider.Object.PluginId,
                    Name = provider.Object.ProviderName,
                    Version = "1.0.0",
                    AssemblyName = Path.GetFileName(Assembly.GetExecutingAssembly().Location),
                    PluginClass = typeof(object).FullName ?? "System.Object"
                },
                provider.Object,
                new PluginAssemblyLoadContext(Assembly.GetExecutingAssembly().Location),
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory))
            .ToList();

        SetPrivateField(pluginManager, "_allPlugins", loadedPlugins);
        SetPrivateField(pluginManager, "_llmProviders", providers.Select(provider => provider.Object).ToList());
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

    private static void InvokePrivateMethod(object target, string methodName, params object[] arguments)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().FullName, methodName);
        method.Invoke(target, arguments);
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
