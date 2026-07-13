using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides dictionary section behavior.
/// </summary>
public partial class DictionarySection : UserControl
{
    /// <summary>
    /// Initializes a new instance of the DictionarySection class.
    /// </summary>
    public DictionarySection() => InitializeComponent();

    private void PrepareAlias_Click(object sender, RoutedEventArgs e)
    {
        OriginalBox.Focus();
        e.Handled = true;
    }

    private void DataManagement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private void OpenTraining_Click(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(TrainingTargetBox.Focus);

    private void EditOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dictionary.CancelEditCommand.Execute(null);
    }

    private void EditOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsWindowViewModel vm)
        {
            vm.Dictionary.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void TrainingOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsWindowViewModel vm)
        {
            vm.Dictionary.Training?.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
