using System.IO;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.PluginSDK;
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
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private PluginHostServices CreateServices(Action? onCapabilitiesChanged = null) =>
        new("test-plugin", _tempDir, _activeWindow.Object, _eventBus.Object,
            _workflows.Object, onCapabilitiesChanged);

    [Fact]
    public void NotifyCapabilitiesChanged_InvokesCallback()
    {
        var callbackInvoked = false;
        var services = CreateServices(onCapabilitiesChanged: () => callbackInvoked = true);

        services.NotifyCapabilitiesChanged();

        Assert.True(callbackInvoked);
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
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }
}
