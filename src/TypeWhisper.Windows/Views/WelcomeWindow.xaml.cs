using System.Windows;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
        InitializeComponent();
    }
}
