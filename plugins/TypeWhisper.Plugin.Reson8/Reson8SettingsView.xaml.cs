using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

public partial class Reson8SettingsView : UserControl
{
    private readonly Reson8Plugin _plugin;
    private bool _suppressChanges;

    public Reson8SettingsView(Reson8Plugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ApiKeyLabel.Text = L("Settings.ApiKeyLabel");
        TestButton.Content = L("Settings.Test");
        RemoveButton.Content = L("Settings.Remove");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        ModelLabel.Text = L("Settings.Model");
        RefreshModelsButton.Content = L("Settings.Refresh");
        AdvancedExpander.Header = L("Settings.Advanced");
        CustomBaseUrlLabel.Text = L("Settings.CustomBaseUrl");
        CustomAuthHeaderLabel.Text = L("Settings.CustomAuthHeader");
        AdvancedHintText.Text = L("Settings.AdvancedHint");

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppressChanges = true;
        try
        {
            ApiKeyBox.Password = _plugin.ApiKey ?? "";
            CustomBaseUrlBox.Text = _plugin.CustomBaseUrl == Reson8Plugin.DefaultBaseUrl ? "" : _plugin.CustomBaseUrl;
            CustomAuthHeaderBox.Text = _plugin.CustomAuthHeader == Reson8Plugin.DefaultAuthHeader ? "" : _plugin.CustomAuthHeader;
            AdvancedExpander.IsExpanded = !string.IsNullOrWhiteSpace(CustomBaseUrlBox.Text)
                || !string.IsNullOrWhiteSpace(CustomAuthHeaderBox.Text);
            PopulateModelPicker();
            UpdateVisibility();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChanges)
            return;

        await _plugin.SetApiKeyAsync(ApiKeyBox.Password);
        StatusText.Text = string.IsNullOrWhiteSpace(ApiKeyBox.Password) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
        UpdateVisibility();
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
            if (valid)
                await RefreshModelsAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
            UpdateVisibility();
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Password = "";
        await _plugin.SetApiKeyAsync("");
        _plugin.SetFetchedCustomModels([]);
        StatusText.Text = "";
        PopulateModelPicker();
        UpdateVisibility();
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressChanges)
            return;

        if (ModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        await RefreshModelsAsync();
    }

    private void OnCustomBaseUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChanges)
            return;

        _plugin.SetCustomBaseUrl(CustomBaseUrlBox.Text);
    }

    private void OnCustomAuthHeaderChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressChanges)
            return;

        _plugin.SetCustomAuthHeader(CustomAuthHeaderBox.Text);
    }

    private async Task RefreshModelsAsync()
    {
        RefreshModelsButton.IsEnabled = false;
        RefreshModelsButton.Content = L("Settings.Refreshing");
        try
        {
            var models = await _plugin.FetchCustomModelsAsync();
            _plugin.SetFetchedCustomModels(models);
            PopulateModelPicker();
            StatusText.Text = L("Settings.ModelsFetched", models.Count);
            StatusText.Foreground = Brushes.Green;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RefreshModelsButton.Content = L("Settings.Refresh");
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private void PopulateModelPicker()
    {
        var models = _plugin.TranscriptionModels.ToList();
        ModelPicker.ItemsSource = models;
        ModelPicker.SelectedItem = models.FirstOrDefault(m => m.Id == _plugin.SelectedModelId)
            ?? models.FirstOrDefault();
        ModelHintText.Text = _plugin.FetchedCustomModels.Count == 0
            ? L("Settings.NoCustomModels")
            : L("Settings.ModelsFetched", _plugin.FetchedCustomModels.Count);
    }

    private void UpdateVisibility()
    {
        ModelsSection.Visibility = _plugin.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = _plugin.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static string FormatFallbackText(string key, params object[] args) =>
        args.Length == 0 ? key : string.Format(key, args);

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) =>
        _plugin.Loc?.GetString(key, args) ?? FormatFallbackText(key, args);
}
