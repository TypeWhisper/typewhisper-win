using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Meta;

/// <summary>
/// Provides the Meta Model API settings view.
/// </summary>
public partial class MetaSettingsView : UserControl
{
    private readonly MetaPlugin _plugin;
    private readonly ReasoningEffortOption[] _reasoningEfforts;
    private bool _isInitializing;
    private bool _hasRefreshedOnLoad;

    /// <summary>
    /// Initializes a new settings view.
    /// </summary>
    public MetaSettingsView(MetaPlugin plugin)
    {
        _plugin = plugin;
        _reasoningEfforts =
        [
            new("minimal", L("Settings.ReasoningMinimal")),
            new("low", L("Settings.ReasoningLow")),
            new("medium", L("Settings.ReasoningMedium")),
            new("high", L("Settings.ReasoningHigh")),
            new("xhigh", L("Settings.ReasoningXHigh")),
        ];

        _isInitializing = true;
        InitializeComponent();
        ApiKeyLabel.Text = L("Settings.ApiKey");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        TestButton.Content = L("Settings.Test");
        RefreshButton.Content = L("Settings.Refresh");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        LlmModelLabel.Text = L("Settings.LlmModel");
        ReasoningEffortLabel.Text = L("Settings.ReasoningEffort");
        ReasoningEffortHintText.Text = L("Settings.ReasoningEffortHint");
        ReasoningEffortPicker.ItemsSource = _reasoningEfforts;
        ReasoningEffortPicker.SelectedValue = plugin.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(plugin.ApiKey))
            ApiKeyBox.Password = plugin.ApiKey;
        PopulateModelPickers();
        UpdateModelSectionVisibility();
        _isInitializing = false;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasRefreshedOnLoad || !_plugin.IsConfigured || _plugin.IsUiAutomation)
            return;

        _hasRefreshedOnLoad = true;
        await RefreshModelsAsync(showSuccess: false);
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        await _plugin.SetApiKeyAsync(ApiKeyBox.Password);
        StatusText.Text = string.IsNullOrWhiteSpace(ApiKeyBox.Password)
            ? ""
            : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
        PopulateModelPickers();
        UpdateModelSectionVisibility();
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        var apiKey = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(apiKey))
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
            if (!await _plugin.ValidateApiKeyAsync(apiKey))
            {
                StatusText.Text = L("Settings.ApiKeyInvalid");
                StatusText.Foreground = Brushes.Red;
                return;
            }

            await _plugin.SetApiKeyAsync(apiKey);
            var catalog = await _plugin.RefreshAvailableModelsAsync();
            PopulateModelPickers();
            StatusText.Text = catalog is null
                ? L("Settings.ApiKeyValid")
                : L(
                    "Settings.ModelsFetched",
                    catalog.TranscriptionModels.Count,
                    catalog.LlmModels.Count);
            StatusText.Foreground = Brushes.Green;
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

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshModelsAsync(showSuccess: true);

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;
        if (TranscriptionModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
            return;
        if (LlmModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectLlmModel(model.Id);
    }

    private void OnReasoningEffortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ReasoningEffortPicker.SelectedValue is not string reasoningEffort)
            return;
        _plugin.SetReasoningEffort(reasoningEffort);
    }

    private async Task RefreshModelsAsync(bool showSuccess)
    {
        if (!_plugin.IsConfigured)
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = L("Settings.RefreshingModels");
        StatusText.Foreground = Brushes.Gray;
        try
        {
            var catalog = await _plugin.RefreshAvailableModelsAsync();
            PopulateModelPickers();
            if (catalog is null)
            {
                StatusText.Text = L("Settings.RefreshFailed");
                StatusText.Foreground = Brushes.Orange;
            }
            else if (showSuccess)
            {
                StatusText.Text = L(
                    "Settings.ModelsFetched",
                    catalog.TranscriptionModels.Count,
                    catalog.LlmModels.Count);
                StatusText.Foreground = Brushes.Green;
            }
            else
            {
                StatusText.Text = "";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Settings.RefreshFailed");
            StatusText.Foreground = Brushes.Orange;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void PopulateModelPickers()
    {
        _isInitializing = true;
        var transcriptionModels = _plugin.TranscriptionModels.ToList();
        TranscriptionModelPicker.ItemsSource = transcriptionModels;
        TranscriptionModelPicker.SelectedItem = transcriptionModels.FirstOrDefault(model =>
                model.Id.Equals(_plugin.SelectedModelId, StringComparison.OrdinalIgnoreCase))
            ?? transcriptionModels.FirstOrDefault();

        var llmModels = _plugin.SupportedModels.ToList();
        LlmModelPicker.ItemsSource = llmModels;
        LlmModelPicker.SelectedItem = llmModels.FirstOrDefault(model =>
                model.Id.Equals(_plugin.SelectedLlmModelId, StringComparison.OrdinalIgnoreCase))
            ?? llmModels.FirstOrDefault();
        ReasoningEffortPicker.SelectedValue = _plugin.ReasoningEffort;

        TranscriptionModelHintText.Text = _plugin.FetchedTranscriptionModelCount > 0
            ? L("Settings.TranscriptionModelsFetched", _plugin.FetchedTranscriptionModelCount)
            : L("Settings.TranscriptionModelFallback");
        LlmModelHintText.Text = _plugin.FetchedLlmModelCount > 0
            ? L("Settings.LlmModelsFetched", _plugin.FetchedLlmModelCount)
            : L("Settings.LlmModelFallback");
        _isInitializing = false;
    }

    private void UpdateModelSectionVisibility()
    {
        ModelsSection.Visibility = _plugin.IsConfigured
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) =>
        _plugin.Loc?.GetString(key, args) ?? string.Format(key, args);

    private sealed record ReasoningEffortOption(string Value, string DisplayName);
}
