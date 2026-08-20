using System.IO;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public class PluginHostServicesTests : IDisposable
{
    private readonly Mock<IActiveWindowService> _activeWindow = new();
    private readonly Mock<IPluginEventBus> _eventBus = new();
    private readonly Mock<IWorkflowService> _workflows = new();
    private readonly string _tempDir;

    public PluginHostServicesTests()
    {
        _workflows.Setup(w => w.Workflows).Returns(new List<TypeWhisper.Core.Models.Workflow>());
        _tempDir = Path.Join(Path.GetTempPath(), $"tw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private PluginHostServices CreateServices(
        Action? onCapabilitiesChanged = null,
        ISettingsService? settings = null,
        string pluginId = "test-plugin",
        bool isUiAutomation = false,
        string? pluginDataRoot = null) =>
        new(pluginId, _tempDir, _activeWindow.Object, _eventBus.Object,
            _workflows.Object, onCapabilitiesChanged, settings,
            pluginDataRoot: pluginDataRoot,
            isUiAutomation: isUiAutomation);

    [Theory]
    [InlineData("com.typewhisper.elevenlabs")]
    [InlineData("com.typewhisper.openai")]
    [InlineData("com.typewhisper.openrouter")]
    [InlineData("com.typewhisper.reson8")]
    [InlineData("com.typewhisper.smallest-ai")]
    [InlineData("com.typewhisper.soniox")]
    [InlineData("com.typewhisper.xai")]
    public async Task LoadSecretAsync_ReturnsPlaceholderForApiKeyScreenshotFixtures(string pluginId)
    {
        var services = CreateServices(
            pluginId: pluginId,
            isUiAutomation: true,
            pluginDataRoot: Path.Join(_tempDir, "fixture-data"));

        var value = await services.LoadSecretAsync("api-key");

        Assert.True(services.IsUiAutomation);
        Assert.Equal("typewhisper-ui-automation-placeholder", value);
    }

    [Fact]
    public async Task LoadSecretAsync_DoesNotReturnPlaceholderOutsideUiAutomation()
    {
        var services = CreateServices(
            pluginId: "com.typewhisper.openai",
            pluginDataRoot: Path.Join(_tempDir, "fixture-data"));

        var value = await services.LoadSecretAsync("api-key");

        Assert.False(services.IsUiAutomation);
        Assert.Null(value);
    }

    [Fact]
    public async Task LoadSecretAsync_PrefersStoredSecretOverScreenshotFixture()
    {
        var services = CreateServices(
            pluginId: "com.typewhisper.openai",
            isUiAutomation: true,
            pluginDataRoot: Path.Join(_tempDir, "fixture-data"));
        Assert.True(
            services.PluginDataDirectory.StartsWith(_tempDir, StringComparison.OrdinalIgnoreCase),
            "The secret-writing test must stay inside its temporary data root.");
        await services.StoreSecretAsync("api-key", "stored-test-secret");

        var value = await services.LoadSecretAsync("api-key");

        Assert.Equal("stored-test-secret", value);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_InvokesCallback()
    {
        var callbackInvoked = false;
        var services = CreateServices(onCapabilitiesChanged: () => callbackInvoked = true);

        services.NotifyCapabilitiesChanged();

        Assert.True(callbackInvoked);
    }

    [Fact]
    public void LivePreviewAppearanceProvider_ReturnsNormalizedGlobalSettings()
    {
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LiveTranscriptionEnabled = false,
            LiveTranscriptionFontSize = 99,
            PreviewBubbleAutoHideMilliseconds = 9999
        });
        var services = CreateServices(settings: settings.Object);

        var provider = Assert.IsAssignableFrom<ILivePreviewAppearanceProvider>(services);

        Assert.False(provider.LiveTranscriptionPreviewEnabled);
        Assert.Equal(AppSettings.MaxLiveTranscriptionFontSize, provider.LiveTranscriptionFontSize);
        Assert.Equal(AppSettings.MaxPreviewBubbleAutoHideMilliseconds, provider.PreviewBubbleAutoHideMilliseconds);
    }

    [Fact]
    public void PluginAssetDirectory_DefaultsToPluginDataDirectory()
    {
        var services = CreateServices();

        Assert.Equal(services.PluginDataDirectory, services.PluginAssetDirectory);
    }

    [Fact]
    public void PluginAssetDirectory_UsesCustomModelStoragePath_WhenConfigured()
    {
        var storageRoot = Path.Join(_tempDir, "model-storage");
        Directory.CreateDirectory(storageRoot);
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelStoragePath = storageRoot
        });

        var services = CreateServices(settings: settings.Object);

        Assert.Equal(
            Path.Join(storageRoot, "PluginData", "test-plugin"),
            services.PluginAssetDirectory);
        Assert.NotEqual(services.PluginDataDirectory, services.PluginAssetDirectory);
    }

    [Fact]
    public void PluginAssetDirectory_ThrowsWhenCustomModelStoragePathIsMissing()
    {
        var storageRoot = Path.Join(_tempDir, "missing-storage");
        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.Current).Returns(new AppSettings
        {
            LocalModelStoragePath = storageRoot
        });
        var services = CreateServices(settings: settings.Object);

        var ex = Assert.Throws<LocalModelStorageUnavailableException>(() => services.PluginAssetDirectory);

        Assert.Contains(storageRoot, ex.Message);
        Assert.False(Directory.Exists(storageRoot));
    }

    [Fact]
    public void NotifyCapabilitiesChanged_WithNoCallback_DoesNotThrow()
    {
        var services = CreateServices();
        var ex = Record.Exception(() => services.NotifyCapabilitiesChanged());
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyCapabilitiesChanged_CallbackInvokedMultipleTimes()
    {
        var callCount = 0;
        var services = CreateServices(onCapabilitiesChanged: () => callCount++);

        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();
        services.NotifyCapabilitiesChanged();

        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Constructor_WithoutCallback_DoesNotThrow()
    {
        var ex = Record.Exception(() => CreateServices());
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("C:\\escaped")]
    [InlineData("..\\escaped")]
    [InlineData("nested/escaped")]
    [InlineData(".. ")]
    [InlineData(". ")]
    [InlineData("plugin.")]
    public void Constructor_RejectsPluginIdsThatCanEscapeTheDataRoot(string pluginId)
    {
        var dataRoot = Path.Join(_tempDir, "plugin-data");

        Assert.Throws<ArgumentException>(() => new PluginHostServices(
            pluginId,
            _tempDir,
            _activeWindow.Object,
            _eventBus.Object,
            _workflows.Object,
            pluginDataRoot: dataRoot));
    }

    [Fact]
    public void Localization_IsAvailable()
    {
        var services = CreateServices();
        Assert.NotNull(services.Localization);
    }

    [Fact]
    public void Localization_ReturnsKeyWhenNoFiles()
    {
        var services = CreateServices();
        Assert.Equal("some.key", services.Localization.GetString("some.key"));
    }

    [Fact]
    public void Localization_AvailableLanguagesEmpty_WhenNoFiles()
    {
        var services = CreateServices();
        Assert.Empty(services.Localization.AvailableLanguages);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"PluginHostServicesTests cleanup failed for '{_tempDir}': {ex}");
        }
    }
}
