using System.Windows;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

/// <summary>
/// Provides the guided spoken formatting profile test.
/// </summary>
public partial class SpokenFormattingVerificationWindow : Window
{
    /// <summary>Initializes a guided spoken formatting test window.</summary>
    public SpokenFormattingVerificationWindow(SpokenFormattingViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void Decision_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
