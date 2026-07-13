using System.Text.RegularExpressions;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SettingsNavigationBadgeTests
{
    [Fact]
    public void SettingsNavigationItem_BadgeTextRaisesPropertyChanged()
    {
        var item = new SettingsNavigationItem(SettingsRoute.Integrations, "Plugins", "\uE943");
        var changedProperties = new List<string?>();
        item.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        item.BadgeText = "1";

        Assert.Equal("1", item.BadgeText);
        Assert.Contains(nameof(SettingsNavigationItem.BadgeText), changedProperties);
    }

    [Fact]
    public void SettingsWindow_BindsSidebarBadgeText()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml");

        Assert.Contains("BadgeText", xaml);
        Assert.Contains("SidebarRouteBadge", xaml);
    }

    [Fact]
    public void SettingsWindow_ExposesStableAutomationIdentifiers()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml");

        Assert.Contains("AutomationProperties.AutomationId=\"SettingsWindow\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"{Binding Route}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding Title}\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"CheckForUpdates\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"OpenSetupWizard\"", xaml);
        Assert.Contains("StringFormat=SettingsSection.{0}", xaml);
    }

    [Theory]
    [InlineData("DashboardSection.xaml")]
    [InlineData("AudioSection.xaml")]
    [InlineData("ShortcutsSection.xaml")]
    [InlineData("FileTranscriptionSection.xaml")]
    [InlineData("RecorderSection.xaml")]
    [InlineData("HistorySection.xaml")]
    [InlineData("DictionarySection.xaml")]
    [InlineData("SnippetsSection.xaml")]
    [InlineData("WorkflowsSection.xaml")]
    [InlineData("PluginsSection.xaml")]
    [InlineData("GeneralSection.xaml")]
    [InlineData("AppearanceSection.xaml")]
    [InlineData("AdvancedSection.xaml")]
    [InlineData("PremiumSection.xaml")]
    [InlineData("LicenseSection.xaml")]
    [InlineData("InfoSection.xaml")]
    public void RegisteredSettingsSections_ExposeAutomationIdsForInteractiveControls(string fileName)
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            fileName);
        var controls = Regex.Matches(
            xaml,
            "<(?:Button|ui:Button|ui:ToggleSwitch|ToggleButton|CheckBox|RadioButton|ComboBox|TextBox|PasswordBox|Slider)(?:\\s|>)[^>]*>",
            RegexOptions.Singleline);

        Assert.Contains("AutomationProperties.AutomationId=\"SettingsSection.", xaml);

        var missing = controls
            .Select(static match => match.Value)
            .Where(static control => !control.Contains("AutomationProperties.AutomationId=", StringComparison.Ordinal))
            .ToArray();

        Assert.True(missing.Length == 0, $"{fileName} has controls without AutomationId:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }
}
