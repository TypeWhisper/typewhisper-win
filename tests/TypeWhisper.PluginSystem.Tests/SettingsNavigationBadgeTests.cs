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
}
