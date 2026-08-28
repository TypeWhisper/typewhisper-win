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
    private bool _suppressPasswordChanged;
    private bool _suppressControlChanged;
    private bool _autoRefreshStarted;
    private CancellationTokenSource? _apiKeyRefreshDebounce;

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
        TranscriptionModeLabel.Text = L("Settings.TranscriptionMode");
        TranscriptionModeHintText.Text = L("Settings.TranscriptionModeHint");

        // Pre-fill password box if API key is already set
        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            _suppressPasswordChanged = true;
            ApiKeyBox.Password = plugin.ApiKey;
            _suppressPasswordChanged = false;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateTranscriptionModePicker();
        UpdateModelsSection();
        if (_autoRefreshStarted || !_plugin.ShouldRefreshModelCatalog(DateTimeOffset.UtcNow))
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
        ScheduleCatalogRefreshAfterApiKeyChange();
    }

    private void ScheduleCatalogRefreshAfterApiKeyChange()
    {
        _apiKeyRefreshDebounce?.Cancel();
        if (!_plugin.IsAvailable)
        {
            _apiKeyRefreshDebounce = null;
            return;
        }

        var debounce = new CancellationTokenSource();
        _apiKeyRefreshDebounce = debounce;
        _ = RefreshCatalogAfterDelayAsync(debounce);
    }

    private async Task RefreshCatalogAfterDelayAsync(CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), debounce.Token);
            if (!ReferenceEquals(_apiKeyRefreshDebounce, debounce)
                || !_plugin.ShouldRefreshModelCatalog(DateTimeOffset.UtcNow))
            {
                return;
            }

            _autoRefreshStarted = true;
            await RefreshModelsAsync(showSuccess: false);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_apiKeyRefreshDebounce, debounce))
                _apiKeyRefreshDebounce = null;
            debounce.Dispose();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _apiKeyRefreshDebounce?.Cancel();
        _apiKeyRefreshDebounce = null;
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
                var catalog = await _plugin.FetchModelCatalogAsync();
                if (catalog is not null && catalogKey is not null)
                    await _plugin.SetFetchedModelCatalogAsync(catalog, catalogKey);

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
            var catalog = await _plugin.FetchModelCatalogAsync();
            if (catalog is null
                || catalogKey is null
                || !await _plugin.SetFetchedModelCatalogAsync(catalog, catalogKey))
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
                StatusText.Text = L(
                    "Settings.ModelsFetched",
                    catalog.LlmModels.Count,
                    catalog.TranscriptionModels.Count);
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
            || _plugin.FetchedTranscriptionModels.Count > 0
            ? L(
                "Settings.ModelsFetchedHint",
                _plugin.FetchedLlmModels.Count,
                _plugin.FetchedTranscriptionModels.Count)
            : L("Settings.DefaultModelsHint");
    }

    private void OnTranscriptionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressControlChanged
            && TranscriptionModePicker.SelectedItem is TranscriptionModeOption option)
        {
            _plugin.SetTranscriptionMode(option.Mode);
        }
    }

    private void PopulateTranscriptionModePicker()
    {
        _suppressControlChanged = true;
        var options = new List<TranscriptionModeOption>
        {
            new(GeminiTranscriptionMode.Smart, L("Settings.ModeSmart")),
            new(GeminiTranscriptionMode.Verbatim, L("Settings.ModeVerbatim")),
        };
        TranscriptionModePicker.ItemsSource = options;
        TranscriptionModePicker.SelectedItem = options.First(option =>
            option.Mode == _plugin.TranscriptionMode);
        _suppressControlChanged = false;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;

    private sealed record TranscriptionModeOption(
        GeminiTranscriptionMode Mode,
        string DisplayName);
}
