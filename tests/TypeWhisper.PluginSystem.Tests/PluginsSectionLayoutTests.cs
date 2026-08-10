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
    public void PluginsSection_ExposesRepairDiagnosisAndActionsInBothPluginLists()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("RepairDiagnosticMessage", xaml);
        Assert.Contains("DiagnosticMessage", xaml);
        Assert.Contains("Plugins.BrokenBadge", xaml);
        Assert.Contains("Command=\"{Binding RepairRegistryPluginCommand}\"", xaml);
        Assert.Contains("Command=\"{Binding RepairCommand}\"", xaml);
        Assert.Contains("StringFormat=IntegrationsRepair.{0}", xaml);
        Assert.Contains("StringFormat=IntegrationsRepairMarketplace.{0}", xaml);
    }

    [Fact]
    public void PluginsSection_ExposesAccessibleSourceAndTrustBadgesInBothPluginLists()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");

        Assert.Contains("Text=\"{Binding ArtifactSourceBadge}\"", xaml);
        Assert.Contains("Text=\"{Binding ArtifactTrustBadge}\"", xaml);
        Assert.Contains("ToolTip=\"{Binding ArtifactTrustTooltip}\"", xaml);
        Assert.Contains("StringFormat=IntegrationsSource.{0}", xaml);
        Assert.Contains("StringFormat=IntegrationsTrust.{0}", xaml);
        Assert.Contains("Text=\"{Binding SourceBadge}\"", xaml);
        Assert.Contains("Text=\"{Binding TrustBadge}\"", xaml);
        Assert.Contains("ToolTip=\"{Binding TrustTooltip}\"", xaml);
        Assert.Contains("StringFormat=IntegrationsMarketplaceSource.{0}", xaml);
        Assert.Contains("StringFormat=IntegrationsMarketplaceTrust.{0}", xaml);
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

    [Fact]
    public void PluginsSection_UsesInstalledAndDiscoverCatalogWithManualGuidance()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml.cs");

        Assert.Contains("AutomationProperties.AutomationId=\"IntegrationsTabInstalled\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"IntegrationsTabDiscover\"", xaml);
        Assert.Contains("Plugins.Discover", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"IntegrationsManualGuidance\"", xaml);
        Assert.Contains("Plugins.ManualPluginFolderPath", xaml);
        Assert.Contains("Plugins.OpenManualPluginFolderCommand", xaml);
        Assert.Contains("PropertyGroupDescription(groupProperty)", codeBehind);
        Assert.Contains("SourceGroupSortOrder", codeBehind);
    }

    [Fact]
    public void PluginsSection_ShowsInstalledPluginsBlockedByHostCompatibility()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "PluginsSection.xaml.cs");

        Assert.Contains("UnavailablePlugins", xaml);
        Assert.Contains("DiagnosticMessage", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"IntegrationsUnavailablePlugins\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"IntegrationCompatibilityReason\"", xaml);
        Assert.Contains("InstalledPluginCount", codeBehind);
    }
}
