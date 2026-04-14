using TypeWhisper.Windows.ViewModels;
using Wpf.Ui.Controls;

namespace TypeWhisper.Windows.Views;

public partial class ProfilesContextWindow : FluentWindow
{
    public ProfilesContextWindow(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
