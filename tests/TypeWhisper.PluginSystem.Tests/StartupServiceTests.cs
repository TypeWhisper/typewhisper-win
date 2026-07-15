using Windows.ApplicationModel;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class StartupServiceTests
{
    [Theory]
    [InlineData(StartupTaskState.Disabled, false)]
    [InlineData(StartupTaskState.DisabledByPolicy, false)]
    [InlineData(StartupTaskState.DisabledByUser, false)]
    [InlineData(StartupTaskState.Enabled, true)]
    [InlineData(StartupTaskState.EnabledByPolicy, true)]
    public void StoreStartupState_MapsToCheckboxState(StartupTaskState state, bool expected)
    {
        Assert.Equal(expected, TypeWhisper.Windows.Services.StartupService.IsStoreStartupEnabled(state));
    }

    [Fact]
    public void StoreManifest_DefaultsStartupTaskToEnabled()
    {
        var manifest = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows.StorePackage",
            "Package.appxmanifest.template");

        Assert.Contains("Category=\"windows.startupTask\"", manifest);
        Assert.Contains("TaskId=\"TypeWhisperStartup\"", manifest);
        Assert.Contains("Enabled=\"true\"", manifest);
    }
}
