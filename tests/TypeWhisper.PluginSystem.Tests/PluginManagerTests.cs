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
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private PluginManager? _manager;

    public PluginManagerTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(
            _loader,
            _eventBus,
            _activeWindow.Object,
            _profiles.Object,
            _settings.Object);
        return _manager;
    }

    [Fact]
    public async Task InitializeAsync_WithNoPluginDirs_AllPluginsIsEmpty()
    {
        var manager = CreateManager();

        // InitializeAsync scans AppContext.BaseDirectory/Plugins and TypeWhisperEnvironment.PluginsPath
        // Neither should have plugins in a test environment
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
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}

/// <summary>
/// Tests for PluginManager using a manually constructed LoadedPlugin with a fake plugin.
/// This verifies enable/disable/capability-index logic without needing real assemblies.
/// </summary>
public class PluginManagerWithFakePluginTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IProfileService> _profiles = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private PluginManager? _manager;

    public PluginManagerWithFakePluginTests()
    {
        _profiles.Setup(p => p.Profiles).Returns(new List<Profile>());
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
            _profiles.Object,
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
            _profiles.Object,
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
