using System.Net;
using System.Net.Http;
using System.Text.Json;
using Moq;
using Moq.Protected;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
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

    private async Task<PluginsViewModel> CreateViewModelAsync()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = "com.typewhisper.openai",
                Name = "OpenAI",
                Version = "1.0.0",
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
                Version = "1.0.0",
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
        var service = new PluginRegistryService(manager, _loader, _settings.Object, CreateMockHttpClient(json));
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
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}
