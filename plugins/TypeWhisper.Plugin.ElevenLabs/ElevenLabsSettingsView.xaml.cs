using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

public partial class ElevenLabsSettingsView : UserControl
{
    private readonly ElevenLabsPlugin _plugin;
    private bool _suppressPasswordChanged;

    public ElevenLabsSettingsView(ElevenLabsPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        TestButton.Content = L("Settings.Test");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        ModelHintText.Text = L("Settings.ModelHint");

        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            _suppressPasswordChanged = true;
            ApiKeyBox.Password = plugin.ApiKey;
            _suppressPasswordChanged = false;
        }

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateModelPicker();
        UpdateModelSectionVisibility();
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
            return;

        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
        UpdateModelSectionVisibility();
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
            StatusText.Text = valid ? L("Settings.ApiKeyValid") : L("Settings.ApiKeyInvalid");
            StatusText.Foreground = valid ? Brushes.Green : Brushes.Red;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
            UpdateModelSectionVisibility();
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptionModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private void PopulateModelPicker()
    {
        var models = _plugin.TranscriptionModels.ToList();
        TranscriptionModelPicker.ItemsSource = models;
        TranscriptionModelPicker.SelectedItem = models
            .FirstOrDefault(m => m.Id == _plugin.SelectedModelId)
            ?? models.FirstOrDefault();
    }

    private void UpdateModelSectionVisibility()
    {
        ModelsSection.Visibility = _plugin.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
