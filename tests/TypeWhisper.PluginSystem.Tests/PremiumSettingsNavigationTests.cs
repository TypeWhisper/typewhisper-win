using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views.Sections;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class PremiumSettingsNavigationTests
{
    [Fact]
    public void SettingsNavigation_RegistersPremiumRouteBeforeLicense()
    {
        var navigation = SettingsNavigationCatalog.Build(key => key);
        var systemRoutes = navigation
            .Single(group => group.Group == SettingsGroup.System)
            .Items
            .Select(item => item.Route)
            .ToList();

        Assert.Contains(SettingsRoute.Premium, systemRoutes);
        Assert.True(
            systemRoutes.IndexOf(SettingsRoute.Premium) <
            systemRoutes.IndexOf(SettingsRoute.License));
        Assert.True(typeof(PremiumSection).IsAssignableTo(typeof(System.Windows.Controls.UserControl)));
    }
}
