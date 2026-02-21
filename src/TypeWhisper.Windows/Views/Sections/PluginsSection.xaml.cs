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

    private void ToggleExpanded_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginItemViewModel item })
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }
}
