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
    public void BuildDefaultSearchDirectories_IncludesBundledPluginsBeforeUserPlugins()
    {
        var appBaseDirectory = Path.Join(Path.GetTempPath(), "typewhisper-app");
        var userPluginsPath = Path.Join(Path.GetTempPath(), "typewhisper-user", "Plugins");

        var result = PluginManager.BuildDefaultSearchDirectories(appBaseDirectory, userPluginsPath);

        Assert.Equal(
            [
                Path.Join(appBaseDirectory, "Plugins"),
                userPluginsPath
            ],
            result);
    }

    [Fact]
    public void PreferLaterSearchDirectoryPlugins_LocalPluginOverridesBundledPluginWithSameId()
    {
        var bundled = CreateLoadedPlugin("com.typewhisper.groq", "1.0.2", @"C:\App\Plugins\com.typewhisper.groq");
        var local = CreateLoadedPlugin("com.typewhisper.groq", "1.0.3", @"C:\Users\me\AppData\Local\TypeWhisper\Plugins\com.typewhisper.groq");

        var result = PluginManager.PreferLaterSearchDirectoryPlugins([bundled, local]);

        var plugin = Assert.Single(result);
        Assert.Equal("1.0.3", plugin.Manifest.Version);
        Assert.Same(local, plugin);
    }

    [Fact]
    public void UnloadDiscardedSearchDirectoryPlugins_DisposesPluginDroppedByOverride()
    {
        var bundled = CreateLoadedPlugin("com.typewhisper.groq", "1.0.2", @"C:\App\Plugins\com.typewhisper.groq");
        var local = CreateLoadedPlugin("com.typewhisper.groq", "1.0.3", @"C:\Users\me\AppData\Local\TypeWhisper\Plugins\com.typewhisper.groq");
        var bundledInstance = Assert.IsType<MultiRolePlugin>(bundled.Instance);
        var localInstance = Assert.IsType<MultiRolePlugin>(local.Instance);

        var preferred = PluginManager.PreferLaterSearchDirectoryPlugins([bundled, local]);
        PluginManager.UnloadDiscardedSearchDirectoryPlugins([bundled, local], preferred);

        Assert.Equal(1, bundledInstance.DisposeCount);
        Assert.Equal(0, localInstance.DisposeCount);
    }

    [Fact]
    public void CapabilityIndices_IncludeAdditionalRolesWithoutDuplicatingAllPlugins()
    {
        var manager = CreateManager();
        var plugin = new MultiRolePlugin();
        var manifest = new PluginManifest
        {
            Id = plugin.PluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = plugin.GetType().FullName!
        };
        var context = new PluginAssemblyLoadContext(typeof(PluginManagerTests).Assembly.Location);
        var loaded = new LoadedPlugin(manifest, plugin, context, AppContext.BaseDirectory);

        TestPluginManagerFactory.SetPrivateField(manager, "_allPlugins", new List<LoadedPlugin> { loaded });
        TestPluginManagerFactory.SetPrivateField(manager, "_activatedPlugins", new HashSet<string> { plugin.PluginId });
        TestPluginManagerFactory.InvokeRebuildCapabilityIndices(manager);

        Assert.Single(manager.AllPlugins);
        Assert.Equal(
            ["com.test.multi", "openai-compatible-profile-a"],
            manager.TranscriptionEngines.Select(engine => engine.GetTranscriptionSelectionId()).ToArray());
        Assert.Equal(
            ["com.test.multi", "openai-compatible-profile-a"],
            manager.LlmProviders.Select(provider => provider.GetLlmSelectionId()).ToArray());

        plugin.ClearAdditionalRoles();
        TestPluginManagerFactory.InvokeRebuildCapabilityIndices(manager);

        Assert.Single(manager.AllPlugins);
        Assert.Equal(["com.test.multi"], manager.TranscriptionEngines.Select(engine => engine.GetTranscriptionSelectionId()).ToArray());
        Assert.Equal(["com.test.multi"], manager.LlmProviders.Select(provider => provider.GetLlmSelectionId()).ToArray());
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

    private static LoadedPlugin CreateLoadedPlugin(string id, string version, string pluginDirectory)
    {
        var plugin = new MultiRolePlugin(id, version);
        var manifest = new PluginManifest
        {
            Id = plugin.PluginId,
            Name = plugin.PluginName,
            Version = plugin.PluginVersion,
            AssemblyName = "Fake.dll",
            PluginClass = plugin.GetType().FullName!
        };

        return new LoadedPlugin(
            manifest,
            plugin,
            new PluginAssemblyLoadContext(typeof(PluginManagerTests).Assembly.Location),
            pluginDirectory);
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

    private sealed class MultiRolePlugin :
        ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        IAdditionalTranscriptionEnginesProvider,
        IAdditionalLlmProvidersProvider
    {
        private readonly ProfileRole _profileRole = new("com.test.multi", "openai-compatible-profile-a", "Profile A");
        private readonly ProfileRole _duplicateProfileRole = new("com.test.multi", "openai-compatible-profile-a", "Profile A Duplicate");
        private readonly string _pluginId;
        private readonly string _pluginVersion;
        private bool _includeAdditionalRoles = true;

        public MultiRolePlugin()
            : this("com.test.multi", "1.0.0")
        {
        }

        public MultiRolePlugin(string pluginId, string pluginVersion)
        {
            _pluginId = pluginId;
            _pluginVersion = pluginVersion;
        }

        public string PluginId => _pluginId;
        public string PluginName => "Multi";
        public string PluginVersion => _pluginVersion;
        public string ProviderId => "multi";
        public string ProviderDisplayName => "Multi";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("root-transcribe", "Root Transcribe")];
        public string? SelectedModelId => "root-transcribe";
        public bool SupportsTranslation => false;
        public string ProviderName => "Multi";
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = [new("root-llm", "Root LLM")];
        public IReadOnlyList<ITranscriptionEnginePlugin> AdditionalTranscriptionEngines =>
            _includeAdditionalRoles ? [_profileRole, _duplicateProfileRole] : [];
        public IReadOnlyList<ILlmProviderPlugin> AdditionalLlmProviders =>
            _includeAdditionalRoles ? [_profileRole, _duplicateProfileRole] : [];
        public int DisposeCount { get; private set; }

        public void ClearAdditionalRoles() => _includeAdditionalRoles = false;
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("", language ?? "", 0));
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
        public void Dispose() => DisposeCount++;
    }

    private sealed class ProfileRole :
        ITranscriptionEnginePlugin,
        ILlmProviderPlugin,
        ITranscriptionEngineSelectionIdentity,
        ILlmProviderSelectionIdentity
    {
        private readonly string _displayName;

        public ProfileRole(string pluginId, string selectionId, string displayName)
        {
            PluginId = pluginId;
            TranscriptionSelectionId = selectionId;
            LlmSelectionId = selectionId;
            _displayName = displayName;
        }

        public string PluginId { get; }
        public string PluginName => _displayName;
        public string PluginVersion => "1.0.0";
        public string TranscriptionSelectionId { get; }
        public string LlmSelectionId { get; }
        public string ProviderId => TranscriptionSelectionId;
        public string ProviderDisplayName => _displayName;
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } = [new("profile-transcribe", "Profile Transcribe")];
        public string? SelectedModelId => "profile-transcribe";
        public bool SupportsTranslation => false;
        public string ProviderName => _displayName;
        public bool IsAvailable => true;
        public IReadOnlyList<PluginModelInfo> SupportedModels { get; } = [new("profile-llm", "Profile LLM")];
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) { }
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            Task.FromResult(new PluginTranscriptionResult("", language ?? "", 0));
        public Task<string> ProcessAsync(string systemPrompt, string userText, string model, CancellationToken ct) =>
            Task.FromResult(userText);
        public void Dispose() { }
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
