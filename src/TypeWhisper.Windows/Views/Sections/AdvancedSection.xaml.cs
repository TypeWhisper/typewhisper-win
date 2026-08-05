using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;
using TypeWhisper.Windows.Views;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides advanced section behavior.
/// </summary>
public partial class AdvancedSection : UserControl
{
    /// <summary>
    /// Initializes a new instance of the AdvancedSection class.
    /// </summary>
    public AdvancedSection() => InitializeComponent();

    private void OnTestSpokenFormattingClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsWindowViewModel { SpokenFormatting.HasSelectedModel: true } viewModel)
            return;

        var dialog = new SpokenFormattingVerificationWindow(viewModel.SpokenFormatting)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
