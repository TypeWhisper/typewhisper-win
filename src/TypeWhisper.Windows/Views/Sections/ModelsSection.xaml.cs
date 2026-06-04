using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

/// <summary>
/// Provides models section behavior.
/// </summary>
public partial class ModelsSection : UserControl
{
    /// <summary>
    /// Initializes a new instance of the ModelsSection class.
    /// </summary>
    public ModelsSection() => InitializeComponent();

    private void BrowseModelStorage_Click(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm)
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Instance["Models.StorageBrowseTitle"]
        };

        var current = vm.ModelManager.ModelStoragePath;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        var accepted = dialog.ShowDialog(window);
        if (accepted == true)
            vm.ModelManager.ModelStoragePath = dialog.FolderName;
    }

    private void Model_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ModelItemViewModel model) return;

        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm) return;

        if (vm.ModelManager.ActivateModelCommand.CanExecute(model.FullId))
            vm.ModelManager.ActivateModelCommand.Execute(model.FullId);
    }
}
