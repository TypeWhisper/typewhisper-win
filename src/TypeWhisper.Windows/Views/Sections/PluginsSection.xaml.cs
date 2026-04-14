using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            vm.IsMarketplaceSelected = false;
            EmptyState.Visibility = vm.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Setup grouping by Category
            var view = CollectionViewSource.GetDefaultView(vm.Plugins);
            if (view.GroupDescriptions.Count == 0)
                view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            if (view.SortDescriptions.Count == 0)
            {
                view.SortDescriptions.Add(new SortDescription("Category", ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
            }
        }
    }

    private void OnInstalledTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Plugins.IsMarketplaceSelected = false;

        TabInstalled.Style = (Style)Resources["ActiveTabButtonStyle"];
        TabMarketplace.Style = (Style)Resources["TabButtonStyle"];
        InstalledPanel.Visibility = Visibility.Visible;
        MarketplacePanel.Visibility = Visibility.Collapsed;
    }

    private void OnMarketplaceTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Plugins.IsMarketplaceSelected = true;

        TabInstalled.Style = (Style)Resources["TabButtonStyle"];
        TabMarketplace.Style = (Style)Resources["ActiveTabButtonStyle"];
        InstalledPanel.Visibility = Visibility.Collapsed;
        MarketplacePanel.Visibility = Visibility.Visible;
    }

    private void OnMarketplacePanelPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (MarketplacePanel.Visibility != Visibility.Visible)
            return;

        MarketplacePanel.ScrollToVerticalOffset(MarketplacePanel.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }
}
