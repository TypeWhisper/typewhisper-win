using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views.Sections;

public partial class ModelsSection : UserControl
{
    private bool _isLoadingKeys;

    public ModelsSection() => InitializeComponent();

    private void ApiKeyPasswordBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox pb) return;
        var providerId = pb.Tag as string;
        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm) return;

        _isLoadingKeys = true;
        pb.Password = providerId switch
        {
            "groq" => vm.ModelManager.GroqApiKey,
            "openai" => vm.ModelManager.OpenAiApiKey,
            _ => ""
        };
        _isLoadingKeys = false;
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingKeys) return;
        if (sender is not PasswordBox pb) return;
        var providerId = pb.Tag as string;
        var window = Window.GetWindow(this);
        if (window?.DataContext is not SettingsWindowViewModel vm) return;

        if (providerId == "groq")
            vm.ModelManager.GroqApiKey = pb.Password;
        else if (providerId == "openai")
            vm.ModelManager.OpenAiApiKey = pb.Password;
    }

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
