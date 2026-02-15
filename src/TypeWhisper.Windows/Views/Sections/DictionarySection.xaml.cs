using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class DictionarySection : UserControl
{
    public DictionarySection() => InitializeComponent();

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out var tab))
        {
            if (DataContext is SettingsWindowViewModel vm)
                vm.Dictionary.SelectedTab = tab;
        }
    }

    private void EditOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is SettingsWindowViewModel vm)
            vm.Dictionary.CancelEditCommand.Execute(null);
    }
}
