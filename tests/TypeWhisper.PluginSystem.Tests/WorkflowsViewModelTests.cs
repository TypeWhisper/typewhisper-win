using System.Windows.Controls;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class WorkflowsViewModelTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly PluginManager _pluginManager;

    public WorkflowsViewModelTests()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        _pluginManager = TestPluginManagerFactory.Create(_settings);
    }

    [Fact]
    public void DefaultProviderOption_ShowsAutoFallbackWhenNoDefaultConfigured()
    {
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Equal("Default AI provider: OpenAI / GPT-5.5 (auto)", defaultOption.DisplayName);
        Assert.Same(defaultOption, sut.SelectedDefaultProvider);
    }

    [Fact]
    public void DefaultProviderOption_ShowsAutoFallbackWhenConfiguredDefaultIsStale()
    {
        _settings.Save(_settings.Current with { DefaultLlmProvider = "plugin:missing:gpt-4o" });
        AddLlmProvider(new FakeLlmProvider(
            "com.test.openai",
            "OpenAI",
            [new PluginModelInfo("gpt-5.5", "GPT-5.5")]));

        var sut = CreateViewModel();

        var defaultOption = Assert.Single(sut.AvailableProviders, option => option.Value is null);
        Assert.Equal("Default AI provider: OpenAI / GPT-5.5 (auto)", defaultOption.DisplayName);
        Assert.Same(defaultOption, sut.SelectedDefaultProvider);
    }

    public void Dispose() => _pluginManager.Dispose();

    private WorkflowsViewModel CreateViewModel()
    {
        var workflows = new Mock<IWorkflowService>();
        workflows.SetupGet(service => service.Workflows).Returns([]);
        workflows.Setup(service => service.NextSortOrder()).Returns(1);

        var activeWindow = new Mock<IActiveWindowService>();
        activeWindow.Setup(service => service.GetBrowserUrl()).Returns((string?)null);

        var history = new Mock<IHistoryService>();
        history.SetupGet(service => service.Records).Returns([]);
        history.Setup(service => service.GetDistinctApps()).Returns([]);

        return new WorkflowsViewModel(
            workflows.Object,
            activeWindow.Object,
            history.Object,
            _settings,
            _pluginManager,
            new ModelManagerService(_pluginManager, _settings),
            new WindowsAppDiscoveryService(history.Object));
    }

    private void AddLlmProvider(FakeLlmProvider provider)
    {
        var manifest = new PluginManifest
        {
            Id = provider.PluginId,
            Name = provider.PluginName,
            Version = provider.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = provider.GetType().FullName!
        };
        var context = new PluginAssemblyLoadContext(typeof(WorkflowsViewModelTests).Assembly.Location);
        var loaded = new LoadedPlugin(manifest, provider, context, AppContext.BaseDirectory);

        TestPluginManagerFactory.SetPrivateField(_pluginManager, "_allPlugins", new List<LoadedPlugin> { loaded });
        TestPluginManagerFactory.SetPrivateField(_pluginManager, "_llmProviders", new List<ILlmProviderPlugin> { provider });
    }

    private sealed class FakeLlmProvider : ILlmProviderPlugin
    {
        public FakeLlmProvider(string pluginId, string providerName, IReadOnlyList<PluginModelInfo> supportedModels)
        {
            PluginId = pluginId;
            PluginName = providerName;
            ProviderName = providerName;
            SupportedModels = supportedModels;
        }

        public string PluginId { get; }
        public string PluginName { get; }
        public string PluginVersion => "1.0.0";
        public string ProviderName { get; }
        public bool IsAvailable { get; set; } = true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; }

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
        public void Dispose() { }
    }
}
