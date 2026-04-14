using TypeWhisper.Windows.ViewModels;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

public partial class WelcomeWindow : FluentWindow
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
        Closed += (_, _) => viewModel.Cleanup();
        InitializeComponent();
    }
}
