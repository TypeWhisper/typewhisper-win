using TypeWhisper.Windows.ViewModels;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides welcome window behavior.
/// </summary>
public partial class WelcomeWindow : FluentWindow
{
    /// <summary>
    /// Initializes a new instance of the WelcomeWindow class.
    /// </summary>
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
        Closed += (_, _) => viewModel.Cleanup();
        InitializeComponent();
    }
}
