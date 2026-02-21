using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class ModelsSection : UserControl
{
    public ModelsSection() => InitializeComponent();

    private void CloudModel_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not CloudModelItemViewModel model) return;
        if (!model.IsAvailable) return;

        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm) return;

        if (vm.ModelManager.SelectCloudModelCommand.CanExecute(model.FullId))
            vm.ModelManager.SelectCloudModelCommand.Execute(model.FullId);
    }
}
