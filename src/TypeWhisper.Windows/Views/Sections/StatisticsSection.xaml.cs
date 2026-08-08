using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Hosts the interactive WPF statistics page.
/// </summary>
public partial class StatisticsSection : UserControl
{
    /// <summary>
    /// Initializes a new statistics section.
    /// </summary>
    public StatisticsSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsWindowViewModel viewModel)
                viewModel.Statistics.Refresh();
        };
    }
}
