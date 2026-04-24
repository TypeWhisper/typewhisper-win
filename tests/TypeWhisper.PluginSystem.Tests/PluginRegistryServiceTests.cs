using System.Net;
using System.Net.Http;
using System.Text.Json;
using Moq;
using Moq.Protected;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginRegistryServiceTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private PluginManager? _manager;

    public PluginRegistryServiceTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<Workflow>());
        _settings.Setup(s => s.Current).Returns(new AppSettings());
    }

    private PluginManager CreateManager()
    {
        _manager = new PluginManager(_loader, _eventBus, _activeWindow.Object, _workflows.Object, _settings.Object);
        return _manager;
    }

    private static HttpClient CreateMockHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson)
            });

        return new HttpClient(handler.Object);
    }

    [Fact]
    public async Task FetchRegistryAsync_DeserializesPlugins()
    {
        var plugins = new[]
        {
            new
            {
                Id = "com.test.plugin",
                Name = "Test Plugin",
                Version = "1.0.0",
                Author = "Tester",
                Description = "A test plugin",
                Size = 1024L,
                DownloadUrl = "https://example.com/plugin.zip",
                RequiresApiKey = false
            }
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("com.test.plugin", result[0].Id);
        Assert.Equal("Test Plugin", result[0].Name);
        Assert.Equal("1.0.0", result[0].Version);
    }

    [Fact]
    public async Task FetchRegistryAsync_CachesResults()
    {
        var plugins = new[] { new { Id = "p1", Name = "P", Version = "1.0", Author = "A", Description = "D", Size = 100L, DownloadUrl = "u", RequiresApiKey = false } };
        var json = JsonSerializer.Serialize(plugins);

        var callCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            });

        var httpClient = new HttpClient(handler.Object);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FetchRegistryAsync();
        await service.FetchRegistryAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FetchRegistryAsync_FiltersIncompatibleVersions()
    {
        var plugins = new[]
        {
            new { Id = "compatible", Name = "OK", Version = "1.0", MinHostVersion = "0.1.0", Author = "A", Description = "D", Size = 100L, DownloadUrl = "u", RequiresApiKey = false },
            new { Id = "incompatible", Name = "Nope", Version = "1.0", MinHostVersion = "999.0.0", Author = "A", Description = "D", Size = 100L, DownloadUrl = "u", RequiresApiKey = false }
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("compatible", result[0].Id);
    }

    [Fact]
    public async Task FetchRegistryAsync_HttpError_ReturnsEmptyList()
    {
        var httpClient = CreateMockHttpClient("", HttpStatusCode.InternalServerError);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Empty(result);
    }

    [Fact]
    public void GetInstallState_NotInstalled_WhenPluginNotLoaded()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object);

        var registryPlugin = new RegistryPlugin
        {
            Id = "com.unknown", Name = "Unknown", Version = "1.0.0",
            Author = "A", Description = "D", Size = 100, DownloadUrl = "u"
        };

        Assert.Equal(PluginInstallState.NotInstalled, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_SetsFlag()
    {
        AppSettings? savedSettings = null;
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = false });

        var httpClient = CreateMockHttpClient("[]");
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FirstRunAutoInstallAsync();

        Assert.NotNull(savedSettings);
        Assert.True(savedSettings!.PluginFirstRunCompleted);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_SkipsWhenAlreadyCompleted()
    {
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = true });

        var httpClient = CreateMockHttpClient("[]");
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FirstRunAutoInstallAsync();

        _settings.Verify(s => s.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    public void Dispose()
    {
        _manager?.Dispose();
    }
}
