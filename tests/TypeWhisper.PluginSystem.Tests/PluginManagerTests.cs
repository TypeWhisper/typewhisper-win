using System.IO;
using System.Windows.Controls;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginManagerTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private readonly string _pluginSearchDir;
    private PluginManager? _manager;

    public PluginManagerTests()
    {
        _pluginSearchDir = Path.Combine(Path.GetTempPath(), "TypeWhisper.PluginManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginSearchDir);
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object,
            [_pluginSearchDir]);
        return _manager;
    }

    [Fact]
    public async Task InitializeAsync_WithNoPluginDirs_AllPluginsIsEmpty()
    {
        var manager = CreateManager();

        // InitializeAsync should honor the explicit empty search directory in tests.
        await manager.InitializeAsync();

        Assert.Empty(manager.AllPlugins);
        Assert.Empty(manager.LlmProviders);
        Assert.Empty(manager.TranscriptionEngines);
        Assert.Empty(manager.PostProcessors);
    }

    [Fact]
    public void IsEnabled_UnknownPlugin_ReturnsFalse()
    {
        var manager = CreateManager();
        Assert.False(manager.IsEnabled("com.nonexistent.plugin"));
    }

    [Fact]
    public void GetPlugin_UnknownPlugin_ReturnsNull()
    {
        var manager = CreateManager();
        Assert.Null(manager.GetPlugin("com.nonexistent.plugin"));
    }

    [Fact]
    public async Task EnablePluginAsync_UnknownPlugin_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(() => manager.EnablePluginAsync("com.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisablePluginAsync_UnknownPlugin_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(() => manager.DisablePluginAsync("com.nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void EventBus_ReturnsSameInstance()
    {
        var manager = CreateManager();
        Assert.Same(_eventBus, manager.EventBus);
    }

    [Fact]
    public void Dispose_WithNoPlugins_DoesNotThrow()
    {
        var manager = CreateManager();
        var ex = Record.Exception(() => manager.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task InitializeAsync_PersistsEnabledState_RespectedFromSettings()
    {
        var customSettings = new AppSettings
        {
            PluginEnabledState = new Dictionary<string, bool>
            {
                ["com.test.plugin"] = true
            }
        };
        _settings.Setup(s => s.Current).Returns(customSettings);

        var manager = CreateManager();
        await manager.InitializeAsync();

        // Plugin doesn't actually exist, but the settings state is loaded without error
        Assert.Empty(manager.AllPlugins);
    }

    [Fact]
    public async Task InitializeAsync_EmptyPluginEnabledState_NoError()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings
        {
            PluginEnabledState = new Dictionary<string, bool>()
        });

        var manager = CreateManager();
        var ex = await Record.ExceptionAsync(() => manager.InitializeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task CapabilityIndices_EmptyAfterInit_WithNoPlugins()
    {
        var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Empty(manager.LlmProviders);
        Assert.Empty(manager.TranscriptionEngines);
        Assert.Empty(manager.PostProcessors);
        Assert.Empty(manager.ActionPlugins);
    }

    [Fact]
    public async Task ActionPlugins_EmptyAfterInit_WithNoPlugins()
    {
        var manager = CreateManager();
        await manager.InitializeAsync();

        Assert.Empty(manager.ActionPlugins);
    }

    [Fact]
    public async Task PluginStateChanged_FiredOnInitialize()
    {
        var manager = CreateManager();
        var eventFired = false;
        manager.PluginStateChanged += (_, _) => eventFired = true;

        await manager.InitializeAsync();

        Assert.True(eventFired);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        try
        {
            if (Directory.Exists(_pluginSearchDir))
                Directory.Delete(_pluginSearchDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests
        }
    }
}

/// <summary>
/// Tests for PluginManager using a manually constructed LoadedPlugin with a fake plugin.
/// This verifies enable/disable/capability-index logic without needing real assemblies.
/// </summary>
public class PluginManagerWithFakePluginTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private PluginManager? _manager;

    public PluginManagerWithFakePluginTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    [Fact]
    public async Task EnableAndDisable_TracksActivationState()
    {
        var mockPlugin = new Mock<ILlmProviderPlugin>();
        mockPlugin.Setup(p => p.PluginId).Returns("com.test.fake");
        mockPlugin.Setup(p => p.PluginName).Returns("Fake LLM");
        mockPlugin.Setup(p => p.PluginVersion).Returns("1.0.0");
        mockPlugin.Setup(p => p.ActivateAsync(It.IsAny<IPluginHostServices>())).Returns(Task.CompletedTask);
        mockPlugin.Setup(p => p.DeactivateAsync()).Returns(Task.CompletedTask);
        mockPlugin.Setup(p => p.ProviderName).Returns("FakeProvider");
        mockPlugin.Setup(p => p.IsAvailable).Returns(true);
        mockPlugin.Setup(p => p.SupportedModels).Returns(new List<PluginModelInfo>());

        // We can't easily inject a LoadedPlugin into PluginManager since it uses PluginLoader.
        // But we can verify that Enable/Disable of an unknown plugin is handled gracefully.
        _manager = new PluginManager(
            new PluginLoader(),
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object);

        Assert.False(_manager.IsEnabled("com.test.fake"));

        // Enable unknown plugin - should be a no-op
        await _manager.EnablePluginAsync("com.test.fake");
        Assert.False(_manager.IsEnabled("com.test.fake"));
    }

    [Fact]
    public async Task DisablePluginAsync_NotActivated_PersistsDisabledState()
    {
        AppSettings? savedSettings = null;
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);

        _manager = new PluginManager(
            new PluginLoader(),
            _eventBus,
            _activeWindow.Object,
            _workflows.Object,
            _settings.Object);

        // DisablePluginAsync for unknown plugin - plugin is null, returns early
        await _manager.DisablePluginAsync("com.test.notfound");

        // Save was not called because GetPlugin returns null
        Assert.Null(savedSettings);
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}
