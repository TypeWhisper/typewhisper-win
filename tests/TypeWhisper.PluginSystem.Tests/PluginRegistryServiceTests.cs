using System.Net;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using Moq.Protected;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginRegistryServiceTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly Mock<ISettingsService> _settings = new();
    private readonly PluginEventBus _eventBus = new();
    private readonly PluginLoader _loader = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly string _pluginsRoot;
    private PluginManager? _manager;

    public PluginRegistryServiceTests()
    {
        _pluginsRoot = Path.Combine(Path.GetTempPath(), "TypeWhisper.PluginRegistryServiceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginsRoot);
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
            [_pluginsRoot],
            Path.Combine(_pluginsRoot, ".plugin-data"));
        return _manager;
    }

    private HttpClient CreateMockHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => TrackDisposable(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson)
            }));

        return TrackDisposable(new HttpClient(handler.Object));
    }

    private HttpClient CreateMockHttpClient(byte[] responseBytes, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => TrackDisposable(new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(responseBytes)
            }));

        return TrackDisposable(new HttpClient(handler.Object));
    }

    private HttpClient CreateRegistryHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage request, CancellationToken _) =>
                TrackDisposable(responseFactory(request)));

        return TrackDisposable(new HttpClient(handler.Object));
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
                Sha256 = new string('A', 64),
                Source = "official",
                Trust = RegistryArtifactTrustValidator.TypeWhisperBuiltTrust,
                SourceRepository = "https://github.com/TypeWhisper/typewhisper-win",
                Attestation = new
                {
                    SchemaVersion = RegistryArtifactTrustValidator.SupportedSchemaVersion,
                    Algorithm = RegistryArtifactTrustValidator.SupportedAlgorithm,
                    KeyId = "future-production-key",
                    SourceCommit = new string('a', 40),
                    Signature = Convert.ToBase64String(new byte[64])
                },
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
        Assert.Equal("official", result[0].Source);
        Assert.Equal(RegistryArtifactTrustValidator.TypeWhisperBuiltTrust, result[0].Trust);
        Assert.Equal("future-production-key", result[0].Attestation?.KeyId);
    }

    [Fact]
    public async Task FetchRegistryAsync_DeserializesMultipleCategories()
    {
        var plugins = new[]
        {
            new
            {
                Id = "com.test.openai",
                Name = "OpenAI",
                Version = "1.0.0",
                Author = "Tester",
                Description = "A multi-capability plugin",
                Category = "transcription",
                Categories = new[] { "transcription", "llm", "tts" },
                Size = 1024L,
                DownloadUrl = "https://example.com/plugin.zip",
                RequiresApiKey = true
            }
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("transcription", result[0].Category);
        Assert.Equal(["transcription", "llm", "tts"], result[0].Categories);
    }

    [Fact]
    public async Task FetchRegistryAsync_DeserializesSingleCategoryString()
    {
        const string json = """
        [
            {
                "id": "com.test.utility",
                "name": "Utility",
                "version": "1.0.0",
                "author": "Tester",
                "description": "A utility plugin",
                "category": "utility",
                "categories": "utility",
                "size": 1024,
                "downloadUrl": "https://example.com/plugin.zip",
                "requiresApiKey": false
            }
        ]
        """;

        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("utility", result[0].Category);
        Assert.Equal(["utility"], result[0].Categories);
    }

    [Fact]
    public async Task FetchRegistryAsync_PreservesLegacyCategoryWhenCategoriesMissing()
    {
        var plugins = new[]
        {
            new
            {
                Id = "com.test.legacy",
                Name = "Legacy",
                Version = "1.0.0",
                Author = "Tester",
                Description = "A legacy plugin",
                Category = "LLM",
                Size = 1024L,
                DownloadUrl = "https://example.com/plugin.zip",
                RequiresApiKey = true
            }
        };

        var json = JsonSerializer.Serialize(plugins);
        var httpClient = CreateMockHttpClient(json);
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Single(result);
        Assert.Equal("LLM", result[0].Category);
        Assert.Null(result[0].Categories);
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
                return TrackDisposable(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            });

        var httpClient = TrackDisposable(new HttpClient(handler.Object));
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        await service.FetchRegistryAsync();
        await service.FetchRegistryAsync();

        Assert.Equal(2, callCount);
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
    public async Task FetchRegistryAsync_MergesDistinctFeedsWithOfficialPrecedenceAndSourceMetadata()
    {
        const string officialJson = """
        [
          {
            "id": "com.test.official",
            "name": "Official",
            "version": "1.0.0",
            "author": "TypeWhisper",
            "description": "Official plugin",
            "size": 10,
            "downloadUrl": "https://example.com/official.zip",
            "source": "community"
          },
          {
            "id": "com.test.shared",
            "name": "Official winner",
            "version": "1.0.0",
            "author": "TypeWhisper",
            "description": "Official plugin",
            "size": 10,
            "downloadUrl": "https://example.com/shared.zip"
          }
        ]
        """;
        const string communityJson = """
        {
          "schemaVersion": 1,
          "futureFeedMetadata": { "preserved": true },
          "plugins": [
            {
              "id": "com.test.community",
              "name": "Community",
              "version": "1.0.0",
              "author": "Community",
              "description": "Community plugin",
              "size": 10,
              "downloadUrl": "https://example.com/community.zip",
              "source": "official",
              "futurePluginMetadata": "ignored"
            },
            {
              "id": "com.test.shared",
              "name": "Community duplicate",
              "version": "9.0.0",
              "author": "Community",
              "description": "Must not replace official",
              "size": 10,
              "downloadUrl": "https://example.com/duplicate.zip"
            },
            {
              "id": "../invalid",
              "name": "Invalid path",
              "version": "1.0.0",
              "author": "Community",
              "description": "Must be ignored",
              "size": 10,
              "downloadUrl": "https://example.com/invalid.zip"
            }
          ]
        }
        """;
        var httpClient = CreateRegistryHttpClient(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("plugins-community-v1.json", StringComparison.Ordinal)
                        ? communityJson
                        : officialJson)
            });
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        Assert.Equal(3, result.Count);
        Assert.Equal("official", result.Single(plugin => plugin.Id == "com.test.official").Source);
        var communityPlugin = result.Single(plugin => plugin.Id == "com.test.community");
        var communityItem = new RegistryPluginItemViewModel(communityPlugin, service);
        Assert.Equal("community", communityPlugin.Source);
        Assert.Equal(Loc.Instance["Plugins.SourceCommunity"], communityItem.SourceBadge);
        Assert.Equal(Loc.Instance["Plugins.TrustUnverified"], communityItem.TrustBadge);
        Assert.Equal("Official winner", result.Single(plugin => plugin.Id == "com.test.shared").Name);
    }

    [Fact]
    public async Task FetchRegistryAsync_FiltersHostPlatformAndArchitectureIncompatibleEntries()
    {
        const string communityJson = """
        {
          "schemaVersion": 1,
          "plugins": [
            {
              "id": "compatible",
              "name": "Compatible",
              "version": "1.0.0",
              "minHostVersion": "1.0.0",
              "platforms": "windows",
              "supportedArchitectures": ["amd64"],
              "author": "A",
              "description": "D",
              "size": 10,
              "downloadUrl": "https://example.com/compatible.zip"
            },
            {
              "id": "future-host",
              "name": "Future host",
              "version": "1.0.0",
              "minHostVersion": "2.0.0",
              "author": "A",
              "description": "D",
              "size": 10,
              "downloadUrl": "https://example.com/future.zip"
            },
            {
              "id": "wrong-platform",
              "name": "Wrong platform",
              "version": "1.0.0",
              "platforms": ["macos"],
              "author": "A",
              "description": "D",
              "size": 10,
              "downloadUrl": "https://example.com/macos.zip"
            },
            {
              "id": "wrong-architecture",
              "name": "Wrong architecture",
              "version": "1.0.0",
              "supportedArchitectures": ["arm64"],
              "author": "A",
              "description": "D",
              "size": 10,
              "downloadUrl": "https://example.com/arm.zip"
            }
          ]
        }
        """;
        var httpClient = CreateRegistryHttpClient(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("plugins-community-v1.json", StringComparison.Ordinal)
                        ? communityJson
                        : "[]")
            });
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            processArchitecture: Architecture.X64,
            hostVersion: new Version(1, 5, 0));

        var result = await service.FetchRegistryAsync();

        var plugin = Assert.Single(result);
        Assert.Equal("compatible", plugin.Id);
        Assert.Equal(["windows"], plugin.Platforms);
        Assert.Equal(["amd64"], plugin.SupportedArchitectures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FetchRegistryAsync_OneFailedFeedDoesNotHideTheOtherFeed(bool failCommunity)
    {
        const string officialJson =
            "[{\"id\":\"official\",\"name\":\"Official\",\"version\":\"1.0.0\",\"author\":\"A\",\"description\":\"D\",\"size\":10,\"downloadUrl\":\"u\"}]";
        const string communityJson =
            "[{\"id\":\"community\",\"name\":\"Community\",\"version\":\"1.0.0\",\"author\":\"A\",\"description\":\"D\",\"size\":10,\"downloadUrl\":\"u\"}]";
        var httpClient = CreateRegistryHttpClient(request =>
        {
            var isCommunity = request.RequestUri!.AbsolutePath.EndsWith(
                "plugins-community-v1.json",
                StringComparison.Ordinal);
            var shouldFail = failCommunity == isCommunity;
            return new HttpResponseMessage(shouldFail ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            {
                Content = new StringContent(isCommunity ? communityJson : officialJson)
            };
        });
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var result = await service.FetchRegistryAsync();

        var plugin = Assert.Single(result);
        Assert.Equal(failCommunity ? "official" : "community", plugin.Id);
        Assert.Equal(failCommunity ? "official" : "community", plugin.Source);
    }

    [Fact]
    public async Task FetchRegistryAsync_UsesHostingMetadataWithoutRejectingFutureFields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "plugins": [
            {
              "id": "com.test.hosting",
              "name": "Hosted",
              "version": "1.0.0",
              "author": "A",
              "description": "D",
              "hosting": "cloud",
              "requiresApiKey": false,
              "futureHostingOptions": { "region": "auto" },
              "size": 10,
              "downloadUrl": "u"
            }
          ]
        }
        """;
        var httpClient = CreateRegistryHttpClient(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.AbsolutePath.EndsWith("plugins-community-v1.json", StringComparison.Ordinal)
                        ? "[]"
                        : json)
            });
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient);

        var registryPlugin = Assert.Single(await service.FetchRegistryAsync());
        var item = new RegistryPluginItemViewModel(registryPlugin, service);

        Assert.Equal("cloud", registryPlugin.Hosting);
        Assert.True(registryPlugin.IsCloudHosted);
        Assert.Equal(Loc.Instance["Plugins.Cloud"], item.LocationBadge);
    }

    [Fact]
    public void GetInstallState_NotInstalled_WhenPluginNotLoaded()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var registryPlugin = new RegistryPlugin
        {
            Id = "com.unknown",
            Name = "Unknown",
            Version = "1.0.0",
            Author = "A",
            Description = "D",
            Size = 100,
            DownloadUrl = "u"
        };

        Assert.Equal(PluginInstallState.NotInstalled, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallState_Installed_WhenDiskManifestMatchesRegistryVersion()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var registryPlugin = CreateRegistryPlugin("com.test.disk-installed", "1.0.2");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.2");

        Assert.Equal(PluginInstallState.Installed, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallState_UpdateAvailable_WhenOnlyDiskManifestIsOlderThanRegistry()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var registryPlugin = CreateRegistryPlugin("com.test.disk-update", "1.0.3");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.2");

        Assert.Equal(PluginInstallState.UpdateAvailable, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallState_PendingRestart_WhenPendingUpdateMatchesRegistryVersion()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var registryPlugin = CreateRegistryPlugin("com.test.pending", "1.0.2");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id), registryPlugin.Id, "1.0.2");

        var plugin = new Mock<ITypeWhisperPlugin>();
        plugin.Setup(p => p.PluginId).Returns(registryPlugin.Id);
        plugin.Setup(p => p.PluginName).Returns(registryPlugin.Name);
        plugin.Setup(p => p.PluginVersion).Returns("1.0.0");
        var manifest = new PluginManifest
        {
            Id = registryPlugin.Id,
            Name = registryPlugin.Name,
            Version = "1.0.0",
            AssemblyName = "TestPlugin.dll",
            PluginClass = "TestPlugin"
        };
        var loadedPlugin = new LoadedPlugin(
            manifest,
            plugin.Object,
            new PluginAssemblyLoadContext(typeof(PluginRegistryServiceTests).Assembly.Location),
            Path.Combine(_pluginsRoot, registryPlugin.Id));
        TestPluginManagerFactory.SetPrivateField(manager, "_allPlugins", new List<LoadedPlugin> { loadedPlugin });

        Assert.Equal(PluginInstallState.PendingRestart, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallState_PendingRestart_WhenPendingUninstallExists()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var registryPlugin = CreateRegistryPlugin("com.test.pending-uninstall", "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.0");
        Directory.CreateDirectory(Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id));

        Assert.Equal(PluginInstallState.PendingRestart, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallState_Bundled_WhenOnlyBundledPluginIsLoaded()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.bundled", "1.0.0");
        var bundledRoot = Path.Combine(_pluginsRoot, "bundled");
        var bundledDirectory = Path.Combine(bundledRoot, registryPlugin.Id);
        WritePluginManifest(bundledDirectory, registryPlugin.Id, registryPlugin.Version);
        TestPluginManagerFactory.SetPrivateField(
            manager,
            "_allPlugins",
            new List<LoadedPlugin>
            {
                CreateLoadedPlugin(registryPlugin.Id, registryPlugin.Version, bundledDirectory)
            });
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            bundledPluginsPath: bundledRoot);

        Assert.Equal(PluginInstallState.Bundled, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public void GetInstallDiagnosis_MissingManifest_IsDeterministicallyBroken()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.missing-manifest", "1.0.0");
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "orphan.txt"), "orphan");
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var first = service.GetInstallDiagnosis(registryPlugin);
        var second = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(first, second);
        Assert.Equal(PluginInstallState.Broken, first.State);
        Assert.Equal(PluginDiagnosticCode.MissingFiles, first.DiagnosticCode);
        Assert.False(first.IsRegistryManaged);
    }

    [Fact]
    public void GetInstallDiagnosis_InvalidManifest_IsBroken()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.invalid-manifest", "1.0.0");
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallState.Broken, diagnosis.State);
        Assert.Equal(PluginDiagnosticCode.InvalidManifest, diagnosis.DiagnosticCode);
    }

    [Fact]
    public async Task GetInstallDiagnosis_InitializedManagerWithoutLoadedPlugin_IsLoadFailure()
    {
        var manager = CreateManager();
        await manager.InitializeLoadedPluginsAsync([], CancellationToken.None);
        var registryPlugin = CreateRegistryPlugin("com.test.load-failure", "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, registryPlugin.Version);
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallState.Broken, diagnosis.State);
        Assert.Equal(PluginDiagnosticCode.LoadFailure, diagnosis.DiagnosticCode);
    }

    [Fact]
    public void GetInstallDiagnosis_StaleStagingDirectory_IsInterruptedInstallation()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.interrupted", "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, registryPlugin.Version);
        Directory.CreateDirectory(Path.Combine(_pluginsRoot, ".staging", registryPlugin.Id + "-abandoned"));
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallState.Broken, diagnosis.State);
        Assert.Equal(PluginDiagnosticCode.InterruptedInstallation, diagnosis.DiagnosticCode);
    }

    [Fact]
    public async Task GetInstallDiagnosis_ReceiptFileSetMismatch_IsIntegrityMismatch()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.integrity", "1.0.0", typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.integrity", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);
        await service.RepairPluginAsync(registryPlugin, allowSourceAdoption: true);
        File.WriteAllText(Path.Combine(pluginDirectory, "unexpected.txt"), "tampered");

        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallState.Broken, diagnosis.State);
        Assert.Equal(PluginDiagnosticCode.IntegrityMismatch, diagnosis.DiagnosticCode);
        Assert.True(diagnosis.IsRegistryManaged);
    }

    [Fact]
    public async Task GetInstallDiagnosis_LockedReceipt_IsPermissionDenied()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.permission", "1.0.0", typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.permission", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);
        await service.RepairPluginAsync(registryPlugin, allowSourceAdoption: true);
        var receiptPath = Path.Combine(_pluginsRoot, ".install-metadata", registryPlugin.Id + ".json");

        using var receiptLock = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallState.Broken, diagnosis.State);
        Assert.Equal(PluginDiagnosticCode.PermissionDenied, diagnosis.DiagnosticCode);
    }

    [Fact]
    public async Task RepairPluginAsync_UnknownSourceRequiresExplicitAdoption()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.adoption", "1.0.0", typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.adoption", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepairPluginAsync(registryPlugin, allowSourceAdoption: false));

        Assert.Equal("{ invalid", File.ReadAllText(Path.Combine(pluginDirectory, "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(_pluginsRoot, ".install-metadata")));
    }

    [Fact]
    public async Task RepairPluginAsync_PackageHashMismatchLeavesExistingFilesUntouched()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.repair-hash", "1.0.0", typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.repair-hash", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData([9, 8, 7, 6]))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        File.WriteAllText(Path.Combine(pluginDirectory, "old.txt"), "old");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepairPluginAsync(registryPlugin, allowSourceAdoption: true));

        Assert.Equal("{ invalid", File.ReadAllText(Path.Combine(pluginDirectory, "manifest.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(pluginDirectory, "old.txt")));
        Assert.False(File.Exists(Path.Combine(_pluginsRoot, ".install-metadata", registryPlugin.Id + ".json")));
    }

    [Fact]
    public async Task RepairPluginAsync_AtomicallyReinstallsAndPreservesSettings()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.repair-success", "1.0.0", typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.repair-success", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        File.WriteAllText(Path.Combine(pluginDirectory, "old.txt"), "old");
        var settingsPath = Path.Combine(_pluginsRoot, ".plugin-data", registryPlugin.Id, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var expectedSettings = JsonSerializer.SerializeToUtf8Bytes(new { keep = "original" });
        File.WriteAllBytes(settingsPath, expectedSettings);
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);

        var result = await service.RepairPluginAsync(registryPlugin, allowSourceAdoption: true);
        var diagnosis = service.GetInstallDiagnosis(registryPlugin);

        Assert.Equal(PluginInstallResult.Installed, result);
        Assert.Equal(PluginInstallState.Installed, diagnosis.State);
        Assert.True(diagnosis.IsRegistryManaged);
        Assert.NotNull(manager.GetPlugin(registryPlugin.Id));
        Assert.True(manager.IsEnabled(registryPlugin.Id));
        Assert.Equal(expectedSettings, File.ReadAllBytes(settingsPath));
        Assert.False(File.Exists(Path.Combine(pluginDirectory, ".typewhisper-install.json")));
        Assert.True(File.Exists(Path.Combine(_pluginsRoot, ".install-metadata", registryPlugin.Id + ".json")));
        Assert.Empty(Directory.EnumerateDirectories(_pluginsRoot, registryPlugin.Id + ".repair-backup-*"));
    }

    [Fact]
    public async Task RepairPluginAsync_LoadFailureRestoresFilesAndSettings()
    {
        var manager = CreateManager();
        var package = CreateLoadablePluginPackage("com.test.repair-rollback", "1.0.0", typeof(FailingRegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.repair-rollback", "1.0.0") with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        var originalManifest = "{ invalid";
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), originalManifest);
        File.WriteAllText(Path.Combine(pluginDirectory, "old.txt"), "old");
        var settingsPath = Path.Combine(_pluginsRoot, ".plugin-data", registryPlugin.Id, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var expectedSettings = JsonSerializer.SerializeToUtf8Bytes(new { keep = "original" });
        File.WriteAllBytes(settingsPath, expectedSettings);
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RepairPluginAsync(registryPlugin, allowSourceAdoption: true));

        Assert.Equal(originalManifest, File.ReadAllText(Path.Combine(pluginDirectory, "manifest.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(pluginDirectory, "old.txt")));
        Assert.Equal(expectedSettings, File.ReadAllBytes(settingsPath));
        Assert.Null(manager.GetPlugin(registryPlugin.Id));
        Assert.False(File.Exists(Path.Combine(_pluginsRoot, ".install-metadata", registryPlugin.Id + ".json")));
        Assert.Empty(Directory.EnumerateDirectories(_pluginsRoot, registryPlugin.Id + ".repair-backup-*"));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_pluginsRoot, ".staging")));
    }

    [Fact]
    public void RegistryPluginItem_BrokenDiagnosisExposesRepairState()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.repair-view-model", "1.0.0");
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(pluginDirectory, "manifest.json"), "{ invalid");
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var item = new RegistryPluginItemViewModel(registryPlugin, service);

        Assert.Equal(PluginInstallState.Broken, item.InstallState);
        Assert.True(item.NeedsRepair);
        Assert.True(item.RepairRequiresSourceAdoption);
        Assert.False(string.IsNullOrWhiteSpace(item.DiagnosticMessage));
        Assert.True(item.RepairCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("com.test/escape")]
    [InlineData("com.test\\escape")]
    public void GetInstallState_InvalidPluginId_RejectsPathLikeIds(string pluginId)
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var registryPlugin = CreateRegistryPlugin(pluginId, "1.0.0");

        Assert.Throws<InvalidOperationException>(() => service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_ReplacesActivePluginDirectoryBeforeLoad()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var pluginId = "com.test.apply-pending";
        var activeDir = Path.Combine(_pluginsRoot, pluginId);
        var pendingDir = Path.Combine(_pluginsRoot, ".pending-updates", pluginId);
        WritePluginManifest(activeDir, pluginId, "1.0.0");
        File.WriteAllText(Path.Combine(activeDir, "active.txt"), "old");
        WritePluginManifest(pendingDir, pluginId, "1.0.2");
        File.WriteAllText(Path.Combine(pendingDir, "pending.txt"), "new");

        await service.ApplyPendingUpdatesAsync();

        Assert.False(Directory.Exists(pendingDir));
        Assert.False(File.Exists(Path.Combine(activeDir, "active.txt")));
        Assert.True(File.Exists(Path.Combine(activeDir, "pending.txt")));
        Assert.Equal("1.0.2", ReadManifest(activeDir).Version);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_DeletesPendingUninstallBeforeLoad()
    {
        var manager = CreateManager();
        var pluginId = "com.test.apply-pending-uninstall";
        var activeDir = Path.Combine(_pluginsRoot, pluginId);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", pluginId);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", pluginId);
        WritePluginManifest(activeDir, pluginId, "1.0.0");
        File.WriteAllText(Path.Combine(activeDir, "active.txt"), "old");
        Directory.CreateDirectory(pendingUninstallDir);
        WritePluginManifest(pendingUpdateDir, pluginId, "1.0.2");
        var replaceCallCount = 0;
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) =>
            {
                replaceCallCount++;
                return Task.CompletedTask;
            });

        await service.ApplyPendingUpdatesAsync();

        Assert.False(Directory.Exists(activeDir));
        Assert.False(Directory.Exists(pendingUninstallDir));
        Assert.False(Directory.Exists(pendingUpdateDir));
        Assert.Equal(0, replaceCallCount);
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_DeletesPendingBundledUninstallBeforeLoad()
    {
        var manager = CreateManager();
        var pluginId = "com.test.apply-pending-bundled-uninstall";
        var bundledPluginsRoot = Path.Combine(_pluginsRoot, "bundled");
        var bundledDir = Path.Combine(bundledPluginsRoot, pluginId);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", pluginId);
        WritePluginManifest(bundledDir, pluginId, "1.0.0");
        File.WriteAllText(Path.Combine(bundledDir, "bundled.txt"), "old");
        Directory.CreateDirectory(pendingUninstallDir);
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            bundledPluginsPath: bundledPluginsRoot);

        await service.ApplyPendingUpdatesAsync();

        Assert.False(Directory.Exists(bundledDir));
        Assert.False(Directory.Exists(pendingUninstallDir));
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_PendingUninstallKeepsMarkerWhenPendingUpdateDeleteFails()
    {
        var manager = CreateManager();
        var pluginId = "com.test.apply-pending-uninstall-delete-fails";
        var activeDir = Path.Combine(_pluginsRoot, pluginId);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", pluginId);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", pluginId);
        WritePluginManifest(activeDir, pluginId, "1.0.0");
        Directory.CreateDirectory(pendingUninstallDir);
        WritePluginManifest(pendingUpdateDir, pluginId, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) => throw new InvalidOperationException("Pending update should be skipped."),
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, pendingUpdateDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        await service.ApplyPendingUpdatesAsync();

        Assert.True(Directory.Exists(activeDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
        Assert.True(Directory.Exists(pendingUpdateDir));
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_PendingUninstallMarkerDeleteFailure_KeepsMarker()
    {
        var manager = CreateManager();
        var pluginId = "com.test.apply-pending-uninstall-marker-delete-fails";
        var activeDir = Path.Combine(_pluginsRoot, pluginId);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", pluginId);
        WritePluginManifest(activeDir, pluginId, "1.0.0");
        Directory.CreateDirectory(pendingUninstallDir);
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, pendingUninstallDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        await service.ApplyPendingUpdatesAsync();

        Assert.False(Directory.Exists(activeDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
    }

    [Fact]
    public async Task ApplyPendingUpdatesAsync_UsesInjectedReplacementDelegate()
    {
        var manager = CreateManager();
        var pluginId = "com.test.apply-pending-delegate";
        var activeDir = Path.Combine(_pluginsRoot, pluginId);
        var pendingDir = Path.Combine(_pluginsRoot, ".pending-updates", pluginId);
        WritePluginManifest(activeDir, pluginId, "1.0.0");
        WritePluginManifest(pendingDir, pluginId, "1.0.2");
        var replaceCallCount = 0;
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            replaceActiveDirectoryAsync: (sourceDirectory, targetDirectory, _) =>
            {
                replaceCallCount++;
                Assert.Equal(pendingDir, sourceDirectory);
                Assert.Equal(activeDir, targetDirectory);
                Directory.Delete(targetDirectory, recursive: true);
                Directory.Move(sourceDirectory, targetDirectory);
                return Task.CompletedTask;
            });

        await service.ApplyPendingUpdatesAsync();

        Assert.Equal(1, replaceCallCount);
        Assert.False(Directory.Exists(pendingDir));
        Assert.Equal("1.0.2", ReadManifest(activeDir).Version);
    }

    [Fact]
    public async Task InstallPluginAsync_StalledDownloadTimesOut()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.stalled-download", "1.0.2");
        var httpClient = TrackDisposable(new HttpClient(new StalledHttpMessageHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            _pluginsRoot,
            downloadInactivityTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.Contains("download timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallPluginAsync_HttpClientTimeoutIsNotReportedAsInactivity()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.http-timeout", "1.0.2");
        var httpClient = TrackDisposable(new HttpClient(new StalledHttpMessageHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        });
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            _pluginsRoot,
            downloadInactivityTimeout: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallPluginAsync(registryPlugin));
    }

    [Fact]
    public async Task InstallPluginAsync_DownloadFailure_LeavesExistingPluginDirectoryUntouched()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.download-failure", "1.0.2");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        File.WriteAllText(Path.Combine(activeDir, "keep.txt"), "keep");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient("download failed", HttpStatusCode.InternalServerError),
            _pluginsRoot);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.True(File.Exists(Path.Combine(activeDir, "keep.txt")));
        Assert.Equal("1.0.0", ReadManifest(activeDir).Version);
    }

    [Fact]
    public async Task InstallPluginAsync_InvalidStagedManifest_DoesNotReplaceActivePluginDirectory()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.invalid-package", "1.0.2");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        File.WriteAllText(Path.Combine(activeDir, "keep.txt"), "keep");
        var package = CreatePluginPackage("com.test.other-plugin", "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.True(File.Exists(Path.Combine(activeDir, "keep.txt")));
        Assert.Equal("1.0.0", ReadManifest(activeDir).Version);
    }

    [Fact]
    public async Task InstallPluginAsync_StoreDistributionRequiresPackageHash()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.store-no-hash", "1.0.2");
        var package = CreatePluginPackage(registryPlugin.Id, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            distributionKind: AppDistributionKind.Store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.Contains("SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, registryPlugin.Id)));
    }

    [Fact]
    public async Task InstallPluginAsync_StoreDistributionRejectsPackageHashMismatch()
    {
        var manager = CreateManager();
        var package = CreatePluginPackage("com.test.store-hash-mismatch", "1.0.2");
        var wrongHash = Convert.ToHexString(SHA256.HashData([9, 8, 7, 6]));
        var registryPlugin = CreateRegistryPlugin("com.test.store-hash-mismatch", "1.0.2") with
        {
            Sha256 = wrongHash
        };
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            distributionKind: AppDistributionKind.Store);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.Contains("SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, registryPlugin.Id)));
    }

    [Fact]
    public async Task InstallPluginAsync_ClaimedTrustWithInvalidSignatureFailsBeforeHttpRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = RegistryArtifactTrustValidatorTests.CreateSignedPlugin(key);
        var registryPlugin = signed with
        {
            Attestation = signed.Attestation! with
            {
                Signature = Convert.ToBase64String(new byte[64])
            }
        };
        var handler = new Mock<HttpMessageHandler>();
        var httpClient = TrackDisposable(new HttpClient(handler.Object));
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            _pluginsRoot,
            artifactTrustValidator: RegistryArtifactTrustValidatorTests.CreateValidator(key));

        var ex = await Assert.ThrowsAsync<RegistryArtifactTrustException>(
            () => service.InstallPluginAsync(registryPlugin));

        Assert.Equal(RegistryArtifactValidationCode.InvalidSignature, ex.ValidationCode);
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, registryPlugin.Id)));
    }

    [Fact]
    public async Task InstallPluginAsync_StrictModeRejectsLegacyEntryBeforeHttpRequest()
    {
        var registryPlugin = CreateRegistryPlugin("com.test.strict-trust", "1.0.2");
        var handler = new Mock<HttpMessageHandler>();
        var httpClient = TrackDisposable(new HttpClient(handler.Object));
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            _pluginsRoot,
            requireArtifactAttestation: true);

        var ex = await Assert.ThrowsAsync<RegistryArtifactTrustException>(
            () => service.InstallPluginAsync(registryPlugin));

        Assert.Equal(RegistryArtifactValidationCode.MissingMetadata, ex.ValidationCode);
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task InstallPluginAsync_VerifiedEntryAlwaysEnforcesSignedPackageHash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePluginPackage("com.typewhisper.example", "1.2.3");
        var registryPlugin = RegistryArtifactTrustValidatorTests.CreateSignedPlugin(
            key,
            packageSha256: Convert.ToHexString(SHA256.HashData([9, 8, 7, 6])),
            packageSize: package.Length);
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            artifactTrustValidator: RegistryArtifactTrustValidatorTests.CreateValidator(key));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InstallPluginAsync(registryPlugin));

        Assert.Contains("SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, registryPlugin.Id)));
    }

    [Fact]
    public async Task InstallPluginAsync_VerifiedEntryRejectsMismatchedContentLength()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreatePluginPackage("com.typewhisper.example", "1.2.3");
        var registryPlugin = RegistryArtifactTrustValidatorTests.CreateSignedPlugin(
            key,
            packageSha256: Convert.ToHexString(SHA256.HashData(package)),
            packageSize: package.Length + 1);
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            artifactTrustValidator: RegistryArtifactTrustValidatorTests.CreateValidator(key));

        var ex = await Assert.ThrowsAsync<RegistryArtifactTrustException>(
            () => service.InstallPluginAsync(registryPlugin));

        Assert.Equal(RegistryArtifactValidationCode.InvalidPackageSize, ex.ValidationCode);
        Assert.False(Directory.Exists(Path.Join(_pluginsRoot, registryPlugin.Id)));
    }

    [Fact]
    public async Task InstallPluginAsync_PersistsVerifiedOriginInsteadOfTrustingCurrentRegistryMetadata()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = CreateLoadablePluginPackage(
            "com.typewhisper.example",
            "1.2.3",
            typeof(RegistryRepairTestPlugin));
        var registryPlugin = RegistryArtifactTrustValidatorTests.CreateSignedPlugin(
            key,
            packageSha256: Convert.ToHexString(SHA256.HashData(package)),
            packageSize: package.Length);
        var validator = RegistryArtifactTrustValidatorTests.CreateValidator(key);
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            pluginDataPath: Path.Combine(_pluginsRoot, ".plugin-data"),
            artifactTrustValidator: validator);

        var installResult = await service.InstallPluginAsync(registryPlugin);
        var unsignedCurrentEntry = CreateRegistryPlugin(registryPlugin.Id, registryPlugin.Version) with
        {
            Name = "Spoofed display metadata"
        };
        var installedTrust = service.GetInstalledArtifactTrustStatus(unsignedCurrentEntry);

        Assert.Equal(PluginInstallResult.Installed, installResult);
        Assert.True(installedTrust.IsVerified);
        Assert.Equal(RegistryArtifactSource.Official, installedTrust.Source);

        var item = new RegistryPluginItemViewModel(unsignedCurrentEntry, service);
        Assert.Equal(Loc.Instance["Plugins.SourceUnknown"], item.SourceBadge);
        Assert.Equal(Loc.Instance["Plugins.TrustUnverified"], item.TrustBadge);
        Assert.Equal(Loc.Instance["Plugins.SourceOfficial"], item.InstalledSourceBadge);
        Assert.Equal(Loc.Instance["Plugins.TrustVerifiedOfficial"], item.InstalledTrustBadge);
    }

    [Fact]
    public async Task InstallPluginAsync_PersistsUnsignedCommunitySourceWithoutGrantingTrust()
    {
        var package = CreateLoadablePluginPackage(
            "com.test.community-origin",
            "1.0.0",
            typeof(RegistryRepairTestPlugin));
        var registryPlugin = CreateRegistryPlugin("com.test.community-origin", "1.0.0") with
        {
            Source = "community",
            Size = package.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(package))
        };
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            pluginDataPath: Path.Combine(_pluginsRoot, ".plugin-data"));

        var installResult = await service.InstallPluginAsync(registryPlugin);
        var installedTrust = service.GetInstalledArtifactTrustStatus(
            registryPlugin with { Source = "official" });
        var item = new RegistryPluginItemViewModel(registryPlugin, service);

        Assert.Equal(PluginInstallResult.Installed, installResult);
        Assert.Equal(RegistryArtifactSource.Community, installedTrust.Source);
        Assert.Equal(RegistryArtifactTrustLevel.Unverified, installedTrust.TrustLevel);
        Assert.Equal(RegistryArtifactValidationCode.MissingMetadata, installedTrust.Code);
        Assert.Equal(Loc.Instance["Plugins.SourceCommunity"], item.InstalledSourceBadge);
        Assert.Equal(Loc.Instance["Plugins.TrustUnverified"], item.InstalledTrustBadge);
    }

    [Fact]
    public async Task FirstRunAutoInstallAsync_StoreDistributionMarksCompletedWithoutFetchingRegistry()
    {
        AppSettings? savedSettings = null;
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = false });
        var handler = new Mock<HttpMessageHandler>();
        var httpClient = TrackDisposable(new HttpClient(handler.Object));
        var manager = CreateManager();
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            httpClient,
            _pluginsRoot,
            distributionKind: AppDistributionKind.Store);

        await service.FirstRunAutoInstallAsync();

        Assert.NotNull(savedSettings);
        Assert.True(savedSettings!.PluginFirstRunCompleted);
        handler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task InstallPluginAsync_ReplacementLockFailure_QueuesPendingRestart()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.locked", "1.0.2");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        var package = CreatePluginPackage(registryPlugin.Id, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) => throw new IOException("locked"));

        var result = await service.InstallPluginAsync(registryPlugin);

        var pendingDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        Assert.Equal(PluginInstallResult.PendingRestart, result);
        Assert.Equal("1.0.0", ReadManifest(activeDir).Version);
        Assert.Equal("1.0.2", ReadManifest(pendingDir).Version);
    }

    [Fact]
    public async Task InstallPluginAsync_ReplacementLockFailure_ClearsStalePendingUninstall()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.locked-reinstall", "1.0.2");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        Directory.CreateDirectory(pendingUninstallDir);
        var package = CreatePluginPackage(registryPlugin.Id, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) => throw new IOException("locked"));

        var result = await service.InstallPluginAsync(registryPlugin);

        Assert.Equal(PluginInstallResult.PendingRestart, result);
        Assert.True(Directory.Exists(pendingUpdateDir));
        Assert.False(Directory.Exists(pendingUninstallDir));
        Assert.Equal("1.0.2", ReadManifest(pendingUpdateDir).Version);
    }

    [Fact]
    public async Task InstallPluginAsync_PendingUninstallDeleteFailure_FailsBeforeReplacingOrQueueing()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.pending-uninstall-locked", "1.0.2");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        Directory.CreateDirectory(pendingUninstallDir);
        var package = CreatePluginPackage(registryPlugin.Id, "1.0.2");
        var replaceCallCount = 0;
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(package),
            _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) =>
            {
                replaceCallCount++;
                return Task.CompletedTask;
            },
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, pendingUninstallDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() => service.InstallPluginAsync(registryPlugin));

        Assert.Equal(0, replaceCallCount);
        Assert.Equal("1.0.0", ReadManifest(activeDir).Version);
        Assert.False(Directory.Exists(pendingUpdateDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
    }

    [Fact]
    public async Task UninstallPluginAsync_DeleteSuccess_ReturnsUninstalledAndRemovesDirectory()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall", "1.0.0");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        WritePluginManifest(pendingUpdateDir, registryPlugin.Id, "1.0.2");
        File.WriteAllText(Path.Combine(activeDir, "active.txt"), "old");
        var service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);

        var result = await service.UninstallPluginAsync(registryPlugin.Id);

        Assert.Equal(PluginUninstallResult.Uninstalled, result);
        Assert.False(Directory.Exists(activeDir));
        Assert.False(Directory.Exists(pendingUpdateDir));
        Assert.Equal(PluginInstallState.NotInstalled, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task UninstallPluginAsync_DeleteSuccess_RemovesBundledDirectory()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall-bundled", "1.0.0");
        var bundledPluginsRoot = Path.Combine(_pluginsRoot, "bundled");
        var bundledDir = Path.Combine(bundledPluginsRoot, registryPlugin.Id);
        WritePluginManifest(bundledDir, registryPlugin.Id, "1.0.0");
        File.WriteAllText(Path.Combine(bundledDir, "bundled.txt"), "old");
        TestPluginManagerFactory.SetPrivateField(
            manager,
            "_allPlugins",
            new List<LoadedPlugin> { CreateLoadedPlugin(registryPlugin.Id, registryPlugin.Version, bundledDir) });
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            bundledPluginsPath: bundledPluginsRoot);

        var result = await service.UninstallPluginAsync(registryPlugin.Id);

        Assert.Equal(PluginUninstallResult.Uninstalled, result);
        Assert.Null(manager.GetPlugin(registryPlugin.Id));
        Assert.False(Directory.Exists(bundledDir));
        Assert.Equal(PluginInstallState.NotInstalled, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task UninstallPluginAsync_DeleteLockFailure_QueuesPendingRestartAndClearsPendingUpdate()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall-locked", "1.0.0");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        WritePluginManifest(pendingUpdateDir, registryPlugin.Id, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, activeDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        var result = await service.UninstallPluginAsync(registryPlugin.Id);

        Assert.Equal(PluginUninstallResult.PendingRestart, result);
        Assert.True(Directory.Exists(activeDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
        Assert.False(Directory.Exists(pendingUpdateDir));
        Assert.Equal(PluginInstallState.PendingRestart, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task UninstallPluginAsync_PendingUninstallMarkerDeleteFailure_DoesNotReturnUninstalled()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall-marker-locked", "1.0.0");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        Directory.CreateDirectory(pendingUninstallDir);
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, pendingUninstallDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() => service.UninstallPluginAsync(registryPlugin.Id));

        Assert.False(Directory.Exists(activeDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
    }

    [Fact]
    public async Task UninstallPluginAsync_PendingUpdateDeleteLockFailure_QueuesPendingRestart()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall-pending-update-locked", "1.0.0");
        var activeDir = Path.Combine(_pluginsRoot, registryPlugin.Id);
        var pendingUpdateDir = Path.Combine(_pluginsRoot, ".pending-updates", registryPlugin.Id);
        var pendingUninstallDir = Path.Combine(_pluginsRoot, ".pending-uninstalls", registryPlugin.Id);
        WritePluginManifest(activeDir, registryPlugin.Id, "1.0.0");
        WritePluginManifest(pendingUpdateDir, registryPlugin.Id, "1.0.2");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            deleteActiveDirectoryAsync: (path, _) =>
            {
                if (PathsEqual(path, pendingUpdateDir))
                    throw new IOException("locked");

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);

                return Task.CompletedTask;
            });

        var result = await service.UninstallPluginAsync(registryPlugin.Id);

        Assert.Equal(PluginUninstallResult.PendingRestart, result);
        Assert.True(Directory.Exists(activeDir));
        Assert.True(Directory.Exists(pendingUpdateDir));
        Assert.True(Directory.Exists(pendingUninstallDir));
        Assert.Equal(PluginInstallState.PendingRestart, service.GetInstallState(registryPlugin));
    }

    [Fact]
    public async Task RegistryPluginItem_RefreshInstallState_ReflectsUninstalledPlugin()
    {
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object);
        var registryPlugin = new RegistryPlugin
        {
            Id = "com.test.plugin",
            Name = "Test Plugin",
            Version = "1.0.0",
            Author = "A",
            Description = "D",
            Size = 100,
            DownloadUrl = "u"
        };

        var plugin = new Mock<ITypeWhisperPlugin>();
        plugin.Setup(p => p.PluginId).Returns(registryPlugin.Id);
        plugin.Setup(p => p.PluginName).Returns(registryPlugin.Name);
        plugin.Setup(p => p.PluginVersion).Returns(registryPlugin.Version);
        plugin.Setup(p => p.DeactivateAsync()).Returns(Task.CompletedTask);

        var manifest = new PluginManifest
        {
            Id = registryPlugin.Id,
            Name = registryPlugin.Name,
            Version = registryPlugin.Version,
            AssemblyName = "TestPlugin.dll",
            PluginClass = "TestPlugin"
        };
        var loadContext = new PluginAssemblyLoadContext(typeof(PluginRegistryServiceTests).Assembly.Location);
        var pluginDirectory = Path.Combine(_pluginsRoot, registryPlugin.Id);
        WritePluginManifest(pluginDirectory, registryPlugin.Id, registryPlugin.Version);
        var loadedPlugin = new LoadedPlugin(manifest, plugin.Object, loadContext, pluginDirectory);
        TestPluginManagerFactory.SetPrivateField(manager, "_allPlugins", new List<LoadedPlugin> { loadedPlugin });

        service = new PluginRegistryService(manager, _loader, _settings.Object, pluginsPath: _pluginsRoot);
        var item = new RegistryPluginItemViewModel(registryPlugin, service);
        Assert.Equal(PluginInstallState.Installed, item.InstallState);

        await manager.UnloadPluginAsync(registryPlugin.Id);
        Directory.Delete(pluginDirectory, recursive: true);
        item.RefreshInstallState();

        Assert.Equal(PluginInstallState.NotInstalled, item.InstallState);
    }

    [Fact]
    public async Task RegistryPluginItem_InstallFailure_ExposesErrorMessage()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;
        Loc.Instance.CurrentLanguage = "en";

        try
        {
            var manager = CreateManager();
            var service = new PluginRegistryService(
                manager,
                _loader,
                _settings.Object,
                CreateMockHttpClient("download failed", HttpStatusCode.InternalServerError));
            var registryPlugin = new RegistryPlugin
            {
                Id = "com.test.install-fails",
                Name = "Broken Plugin",
                Version = "1.0.0",
                Author = "A",
                Description = "D",
                Size = 100,
                DownloadUrl = "https://example.com/broken.zip"
            };

            var item = new RegistryPluginItemViewModel(registryPlugin, service);

            await item.InstallCommand.ExecuteAsync(null);

            Assert.Equal(PluginInstallState.NotInstalled, item.InstallState);
            Assert.False(item.IsWorking);
            Assert.True(item.HasInstallError);
            Assert.Contains("Install failed", item.InstallErrorMessage);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
        }
    }

    [Fact]
    public async Task RegistryPluginItem_UpdatePendingRestart_ExposesPendingState()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.pending-vm", "1.0.2");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.0");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            CreateMockHttpClient(CreatePluginPackage(registryPlugin.Id, "1.0.2")),
            _pluginsRoot,
            replaceActiveDirectoryAsync: (_, _, _) => throw new IOException("locked"));
        var item = new RegistryPluginItemViewModel(registryPlugin, service);

        await item.UpdateCommand.ExecuteAsync(null);

        Assert.Equal(PluginInstallState.PendingRestart, item.InstallState);
        Assert.False(item.HasInstallError);
    }

    [Fact]
    public async Task RegistryPluginItem_UninstallPendingRestart_ExposesPendingState()
    {
        var manager = CreateManager();
        var registryPlugin = CreateRegistryPlugin("com.test.uninstall-pending-vm", "1.0.0");
        WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.0");
        var service = new PluginRegistryService(
            manager,
            _loader,
            _settings.Object,
            pluginsPath: _pluginsRoot,
            deleteActiveDirectoryAsync: (_, _) => throw new IOException("locked"));
        var item = new RegistryPluginItemViewModel(registryPlugin, service);

        await item.UninstallCommand.ExecuteAsync(null);

        Assert.Equal(PluginInstallState.PendingRestart, item.InstallState);
        Assert.False(item.HasInstallError);
    }

    [Fact]
    public async Task RegistryPluginItem_UninstallFailure_ExposesErrorMessage()
    {
        Loc.Instance.Initialize();
        var previousLanguage = Loc.Instance.CurrentLanguage;
        Loc.Instance.CurrentLanguage = "en";

        try
        {
            var manager = CreateManager();
            var registryPlugin = CreateRegistryPlugin("com.test.uninstall-fails", "1.0.0");
            WritePluginManifest(Path.Combine(_pluginsRoot, registryPlugin.Id), registryPlugin.Id, "1.0.0");
            var service = new PluginRegistryService(
                manager,
                _loader,
                _settings.Object,
                pluginsPath: _pluginsRoot,
                deleteActiveDirectoryAsync: (_, _) => throw new InvalidOperationException("boom"));
            var item = new RegistryPluginItemViewModel(registryPlugin, service);

            await item.UninstallCommand.ExecuteAsync(null);

            Assert.Equal(PluginInstallState.Installed, item.InstallState);
            Assert.False(item.IsWorking);
            Assert.True(item.HasInstallError);
            Assert.Contains("Uninstall failed", item.InstallErrorMessage);
        }
        finally
        {
            Loc.Instance.CurrentLanguage = previousLanguage;
        }
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
    public async Task FirstRunAutoInstallAsync_DoesNotFetchOrInstallMarketplacePlugins()
    {
        AppSettings? savedSettings = null;
        _settings.Setup(s => s.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(s => savedSettings = s);
        _settings.Setup(s => s.Current).Returns(new AppSettings { PluginFirstRunCompleted = false });

        var registryJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = "com.test.auto-install",
                Name = "Auto Install",
                Version = "1.0.0",
                Author = "Tester",
                Description = "Should not be installed automatically",
                Size = 1024L,
                DownloadUrl = "https://example.com/auto-install.zip",
                RequiresApiKey = false
            }
        });
        var requestCount = 0;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                requestCount++;
                return TrackDisposable(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(registryJson)
                });
            });
        var httpClient = TrackDisposable(new HttpClient(handler.Object));
        var manager = CreateManager();
        var service = new PluginRegistryService(manager, _loader, _settings.Object, httpClient, pluginsPath: _pluginsRoot);

        await service.FirstRunAutoInstallAsync();

        Assert.Equal(0, requestCount);
        Assert.NotNull(savedSettings);
        Assert.True(savedSettings!.PluginFirstRunCompleted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_pluginsRoot));
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
        foreach (var disposable in _disposables)
            disposable.Dispose();

        _disposables.Clear();
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

    private static RegistryPlugin CreateRegistryPlugin(string id, string version) => new()
    {
        Id = id,
        Name = "Test Plugin",
        Version = version,
        Author = "A",
        Description = "D",
        Size = 100,
        DownloadUrl = "https://example.com/plugin.zip"
    };

    private T TrackDisposable<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
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
        File.WriteAllBytes(Path.Combine(pluginDir, manifest.AssemblyName), [1, 2, 3, 4]);
    }

    private static LoadedPlugin CreateLoadedPlugin(string id, string version, string pluginDirectory)
    {
        var plugin = new Mock<ITypeWhisperPlugin>();
        plugin.Setup(p => p.PluginId).Returns(id);
        plugin.Setup(p => p.PluginName).Returns("Test Plugin");
        plugin.Setup(p => p.PluginVersion).Returns(version);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Test Plugin",
            Version = version,
            AssemblyName = "TestPlugin.dll",
            PluginClass = "TestPlugin"
        };

        return new LoadedPlugin(
            manifest,
            plugin.Object,
            new PluginAssemblyLoadContext(typeof(PluginRegistryServiceTests).Assembly.Location),
            pluginDirectory);
    }

    private static PluginManifest ReadManifest(string pluginDir)
    {
        var json = File.ReadAllText(Path.Combine(pluginDir, "manifest.json"));
        return JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static byte[] CreatePluginPackage(string id, string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.Write(JsonSerializer.Serialize(new PluginManifest
                {
                    Id = id,
                    Name = "Test Plugin",
                    Version = version,
                    AssemblyName = "TestPlugin.dll",
                    PluginClass = "TestPlugin"
                }));
            }

            var assembly = archive.CreateEntry("TestPlugin.dll");
            using var assemblyStream = assembly.Open();
            assemblyStream.Write([1, 2, 3, 4]);
        }

        return stream.ToArray();
    }

    private static byte[] CreateLoadablePluginPackage(string id, string version, Type pluginType)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var assemblyPath = pluginType.Assembly.Location;
            var assemblyName = Path.GetFileName(assemblyPath);
            var manifest = archive.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
            {
                writer.Write(JsonSerializer.Serialize(new PluginManifest
                {
                    Id = id,
                    Name = "Repair Test Plugin",
                    Version = version,
                    AssemblyName = assemblyName,
                    PluginClass = pluginType.FullName!
                }));
            }

            var assembly = archive.CreateEntry(assemblyName);
            using var assemblyStream = assembly.Open();
            using var source = File.OpenRead(assemblyPath);
            source.CopyTo(assemblyStream);
        }

        return stream.ToArray();
    }

    private sealed class StalledHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public sealed class RegistryRepairTestPlugin : ITypeWhisperPlugin
{
    public string PluginId => "com.test.repair";
    public string PluginName => "Repair Test Plugin";
    public string PluginVersion => "1.0.0";
    public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
    public Task DeactivateAsync() => Task.CompletedTask;
    public System.Windows.Controls.UserControl? CreateSettingsView() => null;
    public void Dispose() { }
}

public sealed class FailingRegistryRepairTestPlugin : ITypeWhisperPlugin
{
    public string PluginId => "com.test.repair-failing";
    public string PluginName => "Failing Repair Test Plugin";
    public string PluginVersion => "1.0.0";

    public Task ActivateAsync(IPluginHostServices host)
    {
        host.SetSetting("candidate", "changed");
        throw new InvalidOperationException("Activation failed for rollback test.");
    }

    public Task DeactivateAsync() => Task.CompletedTask;
    public System.Windows.Controls.UserControl? CreateSettingsView() => null;
    public void Dispose() { }
}
