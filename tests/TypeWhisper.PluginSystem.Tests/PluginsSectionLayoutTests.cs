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
    public void PluginsSection_OffersMarketplaceUpdates()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("Command=\"{Binding UpdateCommand}\"", xaml);
        Assert.Contains("ConverterParameter=UpdateAvailable", xaml);
    }

    [Fact]
    public void PluginsSection_OpensPluginSettingsInModalInsteadOfInline()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("Click=\"OnInstallPluginClick\"", xaml);
        Assert.Contains("Click=\"OnPluginSettingsClick\"", xaml);
        Assert.Contains("Visibility=\"{Binding HasSettings", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding SettingsAutomationName}\"", xaml);
        Assert.DoesNotContain("<ContentControl Content=\"{Binding SettingsView}\"", xaml);
        Assert.DoesNotContain("<ui:CardExpander", xaml);
        Assert.DoesNotContain("IsExpanded=\"{Binding IsExpanded", xaml);
    }
}
