using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.AssemblyAi;

public partial class AssemblyAiSettingsView : UserControl
{
    private readonly AssemblyAiPlugin _plugin;

    public AssemblyAiSettingsView(AssemblyAiPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        TestButton.Content = L("Settings.Test");

        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            ApiKeyBox.Password = plugin.ApiKey;
        }
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await _plugin.ValidateApiKeyAsync(key);
            if (valid)
            {
                StatusText.Text = L("Settings.ApiKeyValid");
                StatusText.Foreground = Brushes.Green;
            }
            else
            {
                StatusText.Text = L("Settings.ApiKeyInvalid");
                StatusText.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
