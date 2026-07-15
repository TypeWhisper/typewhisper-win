namespace TypeWhisper.PluginSystem.Tests;

public sealed class WelcomeWindowLayoutTests
{
    [Fact]
    public void WelcomeWindow_ProvidesInlineProviderConfiguration()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("SelectedModelNeedsConfiguration", xaml);
        Assert.Contains("SelectedModelSettingsView", xaml);
        Assert.Contains("Welcome.SelectedModelConfigurationTitle", xaml);
    }

    [Fact]
    public void WelcomeWindow_ShowsRecommendedCloudInstallFeedback()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("InstallRecommendedCloudCommand", xaml);
        Assert.Contains("RecommendedCloudPlugin.IsWorking", xaml);
        Assert.Contains("RecommendedCloudPlugin.Progress", xaml);
        Assert.Contains("RecommendedCloudPlugin.InstallErrorMessage", xaml);
    }

    [Fact]
    public void WelcomeWindow_ShowsPendingRestartStateForRecommendedPlugins()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("ConverterParameter=PendingRestart", xaml);
        Assert.Contains("Plugins.RestartRequiredBadge", xaml);
        Assert.DoesNotContain("Welcome.RestartRequired", xaml);
    }

    [Fact]
    public void WelcomeWindow_ShowsAutostartAndOmitsWritingFocus()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("WelcomeAutostart", xaml);
        Assert.Contains("IsChecked=\"{Binding AutostartEnabled}\"", xaml);
        Assert.DoesNotContain("Welcome.IndustryHeader", xaml);
        Assert.DoesNotContain("IndustryPresets", xaml);
        Assert.DoesNotContain("SelectedIndustryPresetId", xaml);
    }

    [Fact]
    public void WelcomeWindow_ShowsUpdateActionsForRecommendedAndMarketplacePlugins()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "WelcomeWindow.xaml");

        Assert.Contains("IsLocalRecommendationUpdateAvailable", xaml);
        Assert.Contains("InstallRecommendedLocalCommand", xaml);
        Assert.Contains("Command=\"{Binding UpdateCommand}\"", xaml);
        Assert.Contains("ConverterParameter=UpdateAvailable", xaml);
    }
}
