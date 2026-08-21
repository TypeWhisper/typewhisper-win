using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.Gemini;

/// <summary>
/// Provides gemini settings view behavior.
/// </summary>
public partial class GeminiSettingsView : UserControl
{
    private readonly GeminiPlugin _plugin;
    private readonly bool _suppressPasswordChanged;
    private bool _autoRefreshStarted;

    /// <summary>
    /// Initializes a new instance of the GeminiSettingsView class.
    /// </summary>
    public GeminiSettingsView(GeminiPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        TestButton.Content = L("Settings.Test");
        RefreshButton.Content = L("Settings.Refresh");
        ModelCatalogLabel.Text = L("Settings.ModelCatalog");

        // Pre-fill password box if API key is already set
        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            _suppressPasswordChanged = true;
            ApiKeyBox.Password = plugin.ApiKey;
            _suppressPasswordChanged = false;
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateModelsSection();
        if (_autoRefreshStarted || !_plugin.IsAvailable || _plugin.FetchedLlmModels.Count > 0)
            return;

        _autoRefreshStarted = true;
        await RefreshModelsAsync(showSuccess: false);
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
            return;

        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
        UpdateModelsSection();
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
                var catalogKey = _plugin.ApiKey;
                var models = await _plugin.FetchLlmModelsAsync();
                if (models is { Count: > 0 } && catalogKey is not null)
                    await _plugin.SetFetchedLlmModelsAsync(models, catalogKey);

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
            UpdateModelsSection();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshModelsAsync(showSuccess: true);

    private async Task RefreshModelsAsync(bool showSuccess)
    {
        if (!_plugin.IsAvailable)
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshButton.IsEnabled = false;
        if (showSuccess)
        {
            StatusText.Text = L("Settings.RefreshingModels");
            StatusText.Foreground = Brushes.Gray;
        }

        try
        {
            var catalogKey = _plugin.ApiKey;
            var models = await _plugin.FetchLlmModelsAsync();
            if (models is not { Count: > 0 }
                || catalogKey is null
                || !await _plugin.SetFetchedLlmModelsAsync(models, catalogKey))
            {
                if (showSuccess)
                {
                    StatusText.Text = L("Settings.RefreshFailed");
                    StatusText.Foreground = Brushes.Orange;
                }
                return;
            }

            if (showSuccess)
            {
                StatusText.Text = L("Settings.ModelsFetched", models.Count);
                StatusText.Foreground = Brushes.Green;
            }
        }
        catch (OperationCanceledException)
        {
            if (showSuccess)
            {
                StatusText.Text = L("Settings.RefreshFailed");
                StatusText.Foreground = Brushes.Orange;
            }
        }
        catch (Exception ex)
        {
            if (showSuccess)
            {
                StatusText.Text = L("Settings.Error", ex.Message);
                StatusText.Foreground = Brushes.Red;
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            UpdateModelsSection();
        }
    }

    private void UpdateModelsSection()
    {
        ModelsSection.Visibility = _plugin.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        ModelCatalogHintText.Text = _plugin.FetchedLlmModels.Count > 0
            ? L("Settings.ModelsFetchedHint", _plugin.FetchedLlmModels.Count)
            : L("Settings.DefaultModelsHint");
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
