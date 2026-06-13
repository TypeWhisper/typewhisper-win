using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Moq;
using Moq.Protected;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginsViewModelMarketplaceFilterTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private readonly string _pluginsRoot = Path.Combine(Path.GetTempPath(), "TypeWhisper.PluginsViewModelTests_" + Guid.NewGuid().ToString("N"));
    private PluginManager? _manager;

    public PluginsViewModelMarketplaceFilterTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    [Fact]
    public async Task MarketplaceFilter_NoSelectionShowsAllPlugins()
    {
        var viewModel = await CreateViewModelAsync();

        Assert.Equal(
            ["Groq", "OpenAI", "OpenAI Compatible", "Supertonic TTS"],
            viewModel.FilteredMarketplacePlugins.Select(plugin => plugin.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task MarketplaceFilter_TextToSpeechShowsEveryPluginWithTtsCapability()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.ToggleMarketplaceCapabilityFilterCommand.Execute("tts");

        Assert.Equal(
            ["OpenAI", "Supertonic TTS"],
            viewModel.FilteredMarketplacePlugins.Select(plugin => plugin.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task MarketplaceFilter_LlmShowsEveryPluginWithLlmCapability()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.ToggleMarketplaceCapabilityFilterCommand.Execute("llm");

        Assert.Equal(
            ["Groq", "OpenAI", "OpenAI Compatible"],
            viewModel.FilteredMarketplacePlugins.Select(plugin => plugin.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task MarketplaceFilter_MultipleSelectionsUseOrLogicWithoutDuplicates()
    {
        var viewModel = await CreateViewModelAsync();

        viewModel.ToggleMarketplaceCapabilityFilterCommand.Execute("llm");
        viewModel.ToggleMarketplaceCapabilityFilterCommand.Execute("tts");

        Assert.Equal(
            ["Groq", "OpenAI", "OpenAI Compatible", "Supertonic TTS"],
            viewModel.FilteredMarketplacePlugins.Select(plugin => plugin.Name).OrderBy(name => name).ToArray());
        Assert.Equal(
            viewModel.FilteredMarketplacePlugins.Select(plugin => plugin.Id).Distinct().Count(),
            viewModel.FilteredMarketplacePlugins.Count);
    }

    [Fact]
    public async Task MarketplaceCapabilityFilters_CountPluginsPerNormalizedCategory()
    {
        var viewModel = await CreateViewModelAsync();

        var counts = viewModel.MarketplaceCapabilityFilters.ToDictionary(filter => filter.Key, filter => filter.Count);

        Assert.Equal(3, counts["transcription"]);
        Assert.Equal(3, counts["llm"]);
        Assert.Equal(2, counts["tts"]);
    }

    [Fact]
    public async Task AvailablePluginUpdateCount_CountsUpdateAvailableMarketplacePlugins()
    {
        WritePluginManifest(Path.Combine(_pluginsRoot, "com.typewhisper.openai"), "com.typewhisper.openai", "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, ".pending-updates", "com.typewhisper.groq"), "com.typewhisper.groq", "1.0.1");

        var viewModel = await CreateViewModelAsync(_pluginsRoot, marketplaceVersion: "1.0.1");

        Assert.Equal(1, viewModel.AvailablePluginUpdateCount);
        Assert.True(viewModel.HasAvailablePluginUpdates);
        Assert.Equal("1", viewModel.PluginUpdateNavigationBadgeText);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PluginUpdateSummaryText));
    }

    [Fact]
    public async Task InstalledPlugin_ExposesMatchingRegistryUpdate()
    {
        var loadedPlugin = CreateLoadedPlugin("com.typewhisper.openai", "OpenAI", "1.0.0");

        var viewModel = await CreateViewModelAsync(null, "1.0.1", [loadedPlugin]);

        var installedPlugin = Assert.Single(viewModel.Plugins, plugin => plugin.Id == "com.typewhisper.openai");
        Assert.True(installedPlugin.HasUpdateAvailable);
        Assert.Equal("1.0.1", installedPlugin.AvailableUpdateVersion);
        Assert.True(installedPlugin.UpdateRegistryPluginCommand.CanExecute(null));
    }

    private async Task<PluginsViewModel> CreateViewModelAsync()
        => await CreateViewModelAsync(null, "1.0.0");

    private async Task<PluginsViewModel> CreateViewModelAsync(
        string? pluginsPath,
        string marketplaceVersion,
        IReadOnlyList<LoadedPlugin>? loadedPlugins = null)
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = "com.typewhisper.openai",
                Name = "OpenAI",
                Version = marketplaceVersion,
                Author = "TypeWhisper",
                Description = "OpenAI transcription, prompts, and text-to-speech.",
                Category = "transcription",
                Categories = new[] { "transcription", "llm", "tts" },
                Size = 1024L,
                DownloadUrl = "https://example.com/openai.zip",
                RequiresApiKey = true
            },
            new
            {
                Id = "com.typewhisper.groq",
                Name = "Groq",
                Version = marketplaceVersion,
                Author = "TypeWhisper",
                Description = "Groq transcription and prompts.",
                Category = "Transcription",
                Categories = new[] { "Transcription", "LLM" },
                Size = 1024L,
                DownloadUrl = "https://example.com/groq.zip",
                RequiresApiKey = true
            },
            new
            {
                Id = "com.typewhisper.openai-compatible",
                Name = "OpenAI Compatible",
                Version = "1.0.0",
                Author = "TypeWhisper",
                Description = "OpenAI-compatible transcription and prompts.",
                Category = "transcription",
                Categories = new[] { "transcription", "llm" },
                Size = 1024L,
                DownloadUrl = "https://example.com/openai-compatible.zip",
                RequiresApiKey = false
            },
            new
            {
                Id = "com.typewhisper.supertonic-tts",
                Name = "Supertonic TTS",
                Version = "1.0.0",
                Author = "TypeWhisper",
                Description = "Local text-to-speech.",
                Category = "text-to-speech",
                Categories = new[] { "text-to-speech" },
                Size = 1024L,
                DownloadUrl = "https://example.com/supertonic.zip",
                RequiresApiKey = false
            }
        });

        var manager = CreateManager();
        if (loadedPlugins is not null)
            TestPluginManagerFactory.SetPrivateField(manager, "_allPlugins", loadedPlugins.ToList());
        var service = new PluginRegistryService(manager, _loader, _settings.Object, CreateMockHttpClient(json), pluginsPath);
        var viewModel = new PluginsViewModel(manager, service);

        await viewModel.RefreshRegistryCommand.ExecuteAsync(null);
        return viewModel;
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(_loader, _eventBus, _activeWindow.Object, _workflows.Object, _settings.Object);
        return _manager;
    }

    private static HttpClient CreateMockHttpClient(string responseJson)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    public void Dispose()
    {
        _manager?.Dispose();
        try
        {
            if (Directory.Exists(_pluginsRoot))
                Directory.Delete(_pluginsRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup in tests.
        }
    }

    private static void WritePluginManifest(string pluginDir, string id, string version)
    {
        Directory.CreateDirectory(pluginDir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Test Plugin",
            Version = version,
            AssemblyName = "TestPlugin.dll",
            PluginClass = "TestPlugin"
        };
        File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static LoadedPlugin CreateLoadedPlugin(string id, string name, string version)
    {
        var plugin = new Mock<ITypeWhisperPlugin>();
        plugin.Setup(p => p.PluginId).Returns(id);
        plugin.Setup(p => p.PluginName).Returns(name);
        plugin.Setup(p => p.PluginVersion).Returns(version);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = name,
            Version = version,
            AssemblyName = "TestPlugin.dll",
            PluginClass = "TestPlugin"
        };

        return new LoadedPlugin(
            manifest,
            plugin.Object,
            new PluginAssemblyLoadContext(typeof(PluginsViewModelMarketplaceFilterTests).Assembly.Location),
            AppContext.BaseDirectory);
    }
}
