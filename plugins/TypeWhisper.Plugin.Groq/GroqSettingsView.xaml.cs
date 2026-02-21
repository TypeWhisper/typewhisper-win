using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.Groq;

public partial class GroqSettingsView : UserControl
{
    private readonly GroqPlugin _plugin;

    public GroqSettingsView(GroqPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // Pre-fill password box if API key is already set
        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            ApiKeyBox.Password = plugin.ApiKey;
        }
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : "Gespeichert";
        StatusText.Foreground = Brushes.Gray;
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText.Text = "Bitte zuerst einen API-Key eingeben";
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = "Teste...";
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await _plugin.ValidateApiKeyAsync(key);
            if (valid)
            {
                StatusText.Text = "API-Key gültig!";
                StatusText.Foreground = Brushes.Green;
            }
            else
            {
                StatusText.Text = "Ungültiger API-Key";
                StatusText.Foreground = Brushes.Red;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Fehler: {ex.Message}";
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
