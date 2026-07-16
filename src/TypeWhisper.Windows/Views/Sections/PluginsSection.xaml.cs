using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides plugins section behavior.
/// </summary>
public partial class PluginsSection : UserControl
{
    private PluginsViewModel? _pluginsViewModel;

    /// <summary>
    /// Initializes a new instance of the PluginsSection class.
    /// </summary>
    public PluginsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = (DataContext as SettingsWindowViewModel)?.Plugins;
        if (vm is not null)
        {
            if (!ReferenceEquals(_pluginsViewModel, vm))
            {
                if (_pluginsViewModel is not null)
                    _pluginsViewModel.PropertyChanged -= OnPluginsPropertyChanged;
                _pluginsViewModel = vm;
                _pluginsViewModel.PropertyChanged += OnPluginsPropertyChanged;
            }

            ApplyTabSelection(vm.IsMarketplaceSelected);
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

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pluginsViewModel is null)
            return;

        _pluginsViewModel.PropertyChanged -= OnPluginsPropertyChanged;
        _pluginsViewModel = null;
    }

    private void OnPluginsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginsViewModel.IsMarketplaceSelected))
            ApplyTabSelection(_pluginsViewModel?.IsMarketplaceSelected ?? false);
    }

    private void OnInstalledTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Plugins.IsMarketplaceSelected = false;
        else
            ApplyTabSelection(false);
    }

    private void OnMarketplaceTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Plugins.IsMarketplaceSelected = true;
        else
            ApplyTabSelection(true);
    }

    private async void OnInstallPluginClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RegistryPluginItemViewModel plugin })
            return;

        await plugin.InstallCommand.ExecuteAsync(null);

        if (plugin.InstallState != PluginInstallState.Installed
            || _pluginsViewModel?.FocusInstalledPlugin(plugin.Id) != true)
        {
            return;
        }

        var installedPlugin = _pluginsViewModel.Plugins.FirstOrDefault(item =>
            string.Equals(item.Id, plugin.Id, StringComparison.OrdinalIgnoreCase));
        if (installedPlugin is not null)
            OpenPluginSettings(installedPlugin);
    }

    private void OnPluginSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginItemViewModel plugin })
            OpenPluginSettings(plugin);
    }

    private void OpenPluginSettings(PluginItemViewModel plugin)
    {
        var owner = Window.GetWindow(this);
        if (owner is null || plugin.SettingsView is null)
            return;

        var dialog = new PluginSettingsWindow(plugin.Name, plugin.SettingsView)
        {
            Owner = owner
        };
        dialog.ShowDialog();
    }

    private void ApplyTabSelection(bool marketplaceSelected)
    {
        TabInstalled.Style = (Style)Resources[marketplaceSelected ? "TabButtonStyle" : "ActiveTabButtonStyle"];
        TabMarketplace.Style = (Style)Resources[marketplaceSelected ? "ActiveTabButtonStyle" : "TabButtonStyle"];
        InstalledPanel.Visibility = marketplaceSelected ? Visibility.Collapsed : Visibility.Visible;
        MarketplacePanel.Visibility = marketplaceSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMarketplacePanelPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (MarketplacePanel.Visibility != Visibility.Visible)
            return;

        MarketplacePanel.ScrollToVerticalOffset(MarketplacePanel.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }
}
