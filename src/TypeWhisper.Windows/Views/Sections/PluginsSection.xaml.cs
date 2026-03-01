using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class PluginsSection : UserControl
{
    public PluginsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = (DataContext as SettingsWindowViewModel)?.Plugins;
        if (vm is not null)
        {
            EmptyState.Visibility = vm.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnInstalledTabClick(object sender, RoutedEventArgs e)
    {
        TabInstalled.Style = (Style)Resources["ActiveTabButtonStyle"];
        TabMarketplace.Style = (Style)Resources["TabButtonStyle"];
        InstalledPanel.Visibility = Visibility.Visible;
        MarketplacePanel.Visibility = Visibility.Collapsed;
    }

    private void OnMarketplaceTabClick(object sender, RoutedEventArgs e)
    {
        TabInstalled.Style = (Style)Resources["TabButtonStyle"];
        TabMarketplace.Style = (Style)Resources["ActiveTabButtonStyle"];
        InstalledPanel.Visibility = Visibility.Collapsed;
        MarketplacePanel.Visibility = Visibility.Visible;
    }
}
