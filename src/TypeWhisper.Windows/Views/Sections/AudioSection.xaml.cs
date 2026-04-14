using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class AudioSection : UserControl
{
    public AudioSection() => InitializeComponent();

    private void Model_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe)
            return;
        if (fe.DataContext is not ModelItemViewModel model)
            return;

        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm)
            return;

        if (vm.ModelManager.ActivateModelCommand.CanExecute(model.FullId))
            vm.ModelManager.ActivateModelCommand.Execute(model.FullId);
    }
}
