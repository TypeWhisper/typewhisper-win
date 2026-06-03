namespace TypeWhisper.PluginSystem.Tests;

public sealed class PremiumSettingsNavigationTests
{
    [Fact]
    public void SettingsNavigation_RegistersPremiumRouteBeforeLicense()
    {
        var navigation = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsNavigation.cs");
        var windowViewModel = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsWindowViewModel.cs");
        var settingsWindow = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml.cs");

        Assert.Contains("Premium", navigation);
        Assert.Contains("SettingsRoute.Premium", windowViewModel);
        Assert.True(
            windowViewModel.IndexOf("SettingsRoute.Premium", StringComparison.Ordinal) <
            windowViewModel.IndexOf("SettingsRoute.License", StringComparison.Ordinal));
        Assert.Contains("PremiumSection", settingsWindow);
    }
}
