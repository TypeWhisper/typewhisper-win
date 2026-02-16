using System.Windows;
using System.Windows.Controls;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.Windows.Views;

public partial class WelcomeWindow : Window
{
    private bool _isLoadingKeys;

    public WelcomeWindow(WelcomeViewModel viewModel)
    {
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
        InitializeComponent();
    }

    private void CloudApiKey_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox pb) return;
        if (DataContext is not WelcomeViewModel vm) return;

        _isLoadingKeys = true;
        pb.Password = (pb.Tag as string) switch
        {
            "groq" => vm.GroqApiKey,
            "openai" => vm.OpenAiApiKey,
            _ => ""
        };
        _isLoadingKeys = false;
    }

    private void CloudApiKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingKeys) return;
        if (sender is not PasswordBox pb) return;
        if (DataContext is not WelcomeViewModel vm) return;

        var providerId = pb.Tag as string;
        if (providerId is not null)
            vm.SaveApiKey(providerId, pb.Password);
    }
}
