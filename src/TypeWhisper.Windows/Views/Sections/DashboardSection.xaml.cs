using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides dashboard section behavior.
/// </summary>
public partial class DashboardSection : UserControl
{
    /// <summary>
    /// Initializes a new instance of the DashboardSection class.
    /// </summary>
    public DashboardSection() => InitializeComponent();

    private void WeekChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 0;
    }

    private void MonthChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 1;
    }

    private void AllTimeChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dashboard.SelectedPeriod = 2;
    }
}
