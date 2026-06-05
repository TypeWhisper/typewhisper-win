using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Groq;

/// <summary>
/// Provides groq settings view behavior.
/// </summary>
public partial class GroqSettingsView : UserControl
{
    private readonly GroqPlugin _plugin;
    private bool _suppressPasswordChanged;

    /// <summary>
    /// Initializes a new instance of the GroqSettingsView class.
    /// </summary>
    public GroqSettingsView(GroqPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        TestButton.Content = L("Settings.Test");
        RefreshButton.Content = L("Settings.Refresh");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        LlmModelLabel.Text = L("Settings.LlmModel");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        LlmModelHintText.Text = L("Settings.LlmModelHint");

        // Pre-fill password box if API key is already set
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
        PopulateModelPickers();
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
            if (valid)
            {
                var models = await _plugin.FetchLlmModelsAsync();
                if (models is not null)
                    _plugin.SetFetchedLlmModels(models);

                PopulateModelPickers();
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
            UpdateModelSectionVisibility();
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptionModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LlmModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectLlmModel(model.Id);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshButton.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var models = await _plugin.FetchLlmModelsAsync();
            if (models is null)
            {
                StatusText.Text = L("Settings.RefreshFailed");
                StatusText.Foreground = Brushes.Orange;
                return;
            }

            _plugin.SetFetchedLlmModels(models);
            PopulateModelPickers();
            StatusText.Text = L("Settings.ModelsFetched", models.Count);
            StatusText.Foreground = Brushes.Green;
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
        var transcriptionModels = _plugin.TranscriptionModels.ToList();
        TranscriptionModelPicker.ItemsSource = transcriptionModels;
        TranscriptionModelPicker.SelectedItem = transcriptionModels
            .FirstOrDefault(m => m.Id == _plugin.SelectedModelId)
            ?? transcriptionModels.FirstOrDefault();

        var llmModels = _plugin.SupportedModels.ToList();
        LlmModelPicker.ItemsSource = llmModels;
        LlmModelPicker.SelectedItem = llmModels
            .FirstOrDefault(m => m.Id == _plugin.SelectedLlmModelId)
            ?? llmModels.FirstOrDefault();

        LlmModelHintText.Text = _plugin.FetchedLlmModels.Count > 0
            ? L("Settings.ModelsFetchedHint", _plugin.FetchedLlmModels.Count)
            : L("Settings.LlmModelHint");
    }

    private void UpdateModelSectionVisibility()
    {
        ModelsSection.Visibility = _plugin.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
