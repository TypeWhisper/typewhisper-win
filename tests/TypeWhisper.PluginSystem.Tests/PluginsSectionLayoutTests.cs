namespace TypeWhisper.PluginSystem.Tests;

public sealed class PluginsSectionLayoutTests
{
    [Fact]
    public void PluginsSection_ShowsPendingRestartStateForMarketplacePlugins()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("ConverterParameter=PendingRestart", xaml);
        Assert.Contains("Plugins.RestartRequiredBadge", xaml);
    }

    [Fact]
    public void PluginsSection_ShowsInstalledUpdateSummaryAndAction()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("Plugins.HasAvailablePluginUpdates", xaml);
        Assert.Contains("Plugins.PluginUpdateSummaryText", xaml);
        Assert.Contains("HasUpdateAvailable", xaml);
        Assert.Contains("UpdateRegistryPluginCommand", xaml);
        Assert.Contains("AvailableUpdateVersion, Mode=OneWay", xaml);
    }

    [Fact]
    public void PluginsSection_DoesNotOfferMarketplaceUpdates()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.DoesNotContain("ConverterParameter=UpdateAvailable", xaml);
    }
}
