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
}
