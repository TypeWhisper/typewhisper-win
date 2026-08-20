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
                {
                    _pluginsViewModel.PropertyChanged -= OnPluginsPropertyChanged;
                    _pluginsViewModel.PluginSettingsRequested -= OnPluginSettingsRequested;
                }
                _pluginsViewModel = vm;
                _pluginsViewModel.PropertyChanged += OnPluginsPropertyChanged;
                _pluginsViewModel.PluginSettingsRequested += OnPluginSettingsRequested;
            }

            ApplyTabSelection(vm.IsMarketplaceSelected);
            UpdateEmptyState();

            ConfigureSourceGrouping(
                CollectionViewSource.GetDefaultView(vm.Plugins),
                nameof(PluginItemViewModel.SourceGroupLabel),
                nameof(PluginItemViewModel.SourceGroupSortOrder));
            ConfigureSourceGrouping(
                CollectionViewSource.GetDefaultView(vm.FilteredMarketplacePlugins),
                nameof(RegistryPluginItemViewModel.SourceGroupLabel),
                nameof(RegistryPluginItemViewModel.SourceGroupSortOrder));
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pluginsViewModel is null)
            return;

        _pluginsViewModel.PropertyChanged -= OnPluginsPropertyChanged;
        _pluginsViewModel.PluginSettingsRequested -= OnPluginSettingsRequested;
        _pluginsViewModel = null;
    }

    private void OnPluginsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginsViewModel.IsMarketplaceSelected))
            ApplyTabSelection(_pluginsViewModel?.IsMarketplaceSelected ?? false);
        else if (e.PropertyName == nameof(PluginsViewModel.InstalledPluginCount))
            UpdateEmptyState();
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _pluginsViewModel?.InstalledPluginCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnInstalledTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Plugins.IsMarketplaceSelected = false;
        else
            ApplyTabSelection(false);
    }

    private void OnDiscoverTabClick(object sender, RoutedEventArgs e)
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

    private void OnPluginSettingsRequested(PluginItemViewModel plugin)
    {
        Program.UiAutomation.RecordEvent($"Plugin settings request: {plugin.Id}");
        OpenPluginSettings(plugin);
    }

    private void OpenPluginSettings(PluginItemViewModel plugin)
    {
        Program.UiAutomation.RecordEvent($"Opening plugin settings: {plugin.Id}");
        var owner = Window.GetWindow(this);
        if (owner is null || plugin.SettingsView is null)
        {
            Program.UiAutomation.RecordEvent(
                $"Plugin settings unavailable: {plugin.Id}, owner={owner is not null}, view={plugin.SettingsView is not null}");
            return;
        }

        var dialog = new PluginSettingsWindow(plugin.Name, plugin.SettingsView)
        {
            Owner = owner
        };

        // A modal WPF provider blocks UIA InvokePattern until the dialog closes.
        // Keep production behavior modal while allowing isolated automation runs
        // to observe and capture the opened top-level window.
        if (Program.UiAutomation.IsEnabled)
        {
            dialog.Loaded += (_, _) => Program.UiAutomation.RecordEvent($"Plugin settings loaded: {plugin.Id}");
            dialog.Show();
        }
        else
            dialog.ShowDialog();
    }

    private void ApplyTabSelection(bool discoverSelected)
    {
        TabInstalled.Style = (Style)Resources[discoverSelected ? "TabButtonStyle" : "ActiveTabButtonStyle"];
        TabDiscover.Style = (Style)Resources[discoverSelected ? "ActiveTabButtonStyle" : "TabButtonStyle"];
        InstalledPanel.Visibility = discoverSelected ? Visibility.Collapsed : Visibility.Visible;
        DiscoverPanel.Visibility = discoverSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ConfigureSourceGrouping(
        ICollectionView view,
        string groupProperty,
        string sortProperty)
    {
        view.GroupDescriptions.Clear();
        view.GroupDescriptions.Add(new PropertyGroupDescription(groupProperty));
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(sortProperty, ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
    }

    private void OnDiscoverPanelPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (DiscoverPanel.Visibility != Visibility.Visible)
            return;

        DiscoverPanel.ScrollToVerticalOffset(DiscoverPanel.VerticalOffset - (e.Delta / 3.0));
        e.Handled = true;
    }
}
