using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.MicrosoftAi;

/// <summary>
/// Provides settings for the Microsoft AI transcription plugin.
/// </summary>
public partial class MicrosoftAiSettingsView : UserControl
{
    private readonly MicrosoftAiPlugin _plugin;
    private bool _isInitializing;

    /// <summary>
    /// Initializes a new Microsoft AI settings view.
    /// </summary>
    public MicrosoftAiSettingsView(MicrosoftAiPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ConnectionLabel.Text = L("Settings.Connection");
        EndpointLabel.Text = L("Settings.Endpoint");
        EndpointBox.ToolTip = L("Settings.EndpointPlaceholder");
        EndpointHintText.Text = L("Settings.EndpointHint");
        ApiKeyLabel.Text = L("Settings.ApiKey");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        SaveButton.Content = L("Settings.Save");
        ModelLabel.Text = L("Settings.Model");
        RefreshModelsButton.Content = L("Settings.RefreshModels");
        ModelHintText.Text = L("Settings.ModelHint");
        TranscriptStyleLabel.Text = L("Settings.TranscriptStyle");
        TranscriptStyleHintText.Text = L("Settings.StyleHint");
        SpeakerDiarizationCheckBox.Content = L("Settings.SpeakerDiarization");
        DiarizationHintText.Text = L("Settings.DiarizationHint");

        _isInitializing = true;
        EndpointBox.Text = plugin.EndpointValue;
        ApiKeyBox.Password = plugin.ApiKey ?? "";
        PopulateModels();
        PopulateTranscriptStyles();
        SpeakerDiarizationCheckBox.IsChecked = plugin.SpeakerDiarizationEnabled;
        UpdateDiarizationAvailability();
        UpdateRegionWarning();
        _isInitializing = false;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        EndpointBox.IsEnabled = false;
        ApiKeyBox.IsEnabled = false;
        try
        {
            _plugin.SetEndpoint(EndpointBox.Text);
            await _plugin.SetApiKeyAsync(ApiKeyBox.Password);
            EndpointBox.Text = _plugin.EndpointValue;
            UpdateRegionWarning();

            if (string.IsNullOrWhiteSpace(EndpointBox.Text) && string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                ShowStatus(L("Settings.Removed"), Brushes.Gray);
            }
            else if (MicrosoftAiPlugin.NormalizeEndpoint(EndpointBox.Text) is null)
            {
                ShowStatus(L("Settings.InvalidEndpoint"), Brushes.DarkOrange);
            }
            else if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                ShowStatus(L("Settings.ApiKeyRequired"), Brushes.DarkOrange);
            }
            else
            {
                ShowStatus(L("Settings.Saved"), Brushes.Green);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(L("Settings.Error", ex.Message), Brushes.Red);
        }
        finally
        {
            SaveButton.IsEnabled = true;
            EndpointBox.IsEnabled = true;
            ApiKeyBox.IsEnabled = true;
        }
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        RefreshModelsButton.IsEnabled = false;
        ShowStatus(L("Settings.RefreshingModels"), Brushes.Gray);
        try
        {
            var refreshed = await _plugin.RefreshModelCatalogAsync();
            PopulateModels();
            ShowStatus(
                L(refreshed ? "Settings.ModelsRefreshed" : "Settings.ModelsUnavailable"),
                refreshed ? Brushes.Green : Brushes.DarkOrange);
        }
        catch (Exception ex)
        {
            ShowStatus(L("Settings.Error", ex.Message), Brushes.Red);
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ModelPicker.SelectedItem is not PluginModelInfo model)
            return;
        _plugin.SelectModel(model.Id);
        UpdateDiarizationAvailability();
        if (!_plugin.SelectedModelSupportsDiarization)
        {
            SpeakerDiarizationCheckBox.IsChecked = false;
            _plugin.SetSpeakerDiarizationEnabled(false);
        }
    }

    private void OnTranscriptStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing
            && TranscriptStylePicker.SelectedItem is TranscriptStyleOption option)
        {
            _plugin.SetTranscriptStyle(option.Style);
        }
    }

    private void OnSpeakerDiarizationChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
        {
            _plugin.SetSpeakerDiarizationEnabled(
                SpeakerDiarizationCheckBox.IsChecked == true);
        }
    }

    private void OnEndpointTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing)
            UpdateRegionWarning();
    }

    private void PopulateModels()
    {
        _isInitializing = true;
        var models = _plugin.TranscriptionModels.ToList();
        ModelPicker.ItemsSource = models;
        ModelPicker.SelectedItem = models.FirstOrDefault(model =>
            string.Equals(model.Id, _plugin.SelectedModelId, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault();
        _isInitializing = false;
        UpdateDiarizationAvailability();
    }

    private void PopulateTranscriptStyles()
    {
        var options = new List<TranscriptStyleOption>
        {
            new(MicrosoftAiTranscriptStyle.Clean, L("Settings.StyleClean")),
            new(MicrosoftAiTranscriptStyle.Verbatim, L("Settings.StyleVerbatim")),
        };
        TranscriptStylePicker.ItemsSource = options;
        TranscriptStylePicker.SelectedItem = options.First(option => option.Style == _plugin.TranscriptStyle);
    }

    private void UpdateDiarizationAvailability() =>
        SpeakerDiarizationCheckBox.IsEnabled = _plugin.SelectedModelSupportsDiarization;

    private void UpdateRegionWarning()
    {
        var endpoint = MicrosoftAiPlugin.NormalizeEndpoint(EndpointBox.Text);
        var region = endpoint is null ? null : MicrosoftAiPlugin.GetUnsupportedMaiRegion(endpoint);
        if (region is null)
        {
            RegionWarningText.Visibility = Visibility.Collapsed;
            RegionWarningText.Text = "";
            return;
        }

        RegionWarningText.Text = L(
            "Settings.RegionUnsupported",
            region,
            string.Join(", ", MicrosoftAiPlugin.SupportedMaiRegions));
        RegionWarningText.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string message, Brush color)
    {
        StatusText.Text = message;
        StatusText.Foreground = color;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? string.Format(key, args);

    private sealed record TranscriptStyleOption(
        MicrosoftAiTranscriptStyle Style,
        string DisplayName);
}
