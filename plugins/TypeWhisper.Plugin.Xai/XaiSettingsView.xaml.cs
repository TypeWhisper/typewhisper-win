using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Xai;

public partial class XaiSettingsView : UserControl
{
    private readonly XaiPlugin _plugin;
    private readonly bool _suppressPasswordChanged;
    private bool _suppressControlChanged;

    public XaiSettingsView(XaiPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        TestButton.Content = L("Settings.Test");
        RefreshModelsButton.Content = L("Settings.Refresh");
        RefreshVoicesButton.Content = L("Settings.Refresh");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        LlmModelLabel.Text = L("Settings.LlmModel");
        TtsVoiceLabel.Text = L("Settings.TtsVoice");
        CustomVoiceLabel.Text = L("Settings.CustomVoiceId");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        LlmModelHintText.Text = L("Settings.LlmModelHint");
        VoiceHintText.Text = L("Settings.VoiceHint");
        CustomVoiceHintText.Text = L("Settings.CustomVoiceHint");
        LowLatencyCheckBox.Content = L("Settings.TtsLowLatency");
        TextNormalizationCheckBox.Content = L("Settings.TtsTextNormalization");

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
        PopulateControls();
        UpdateSettingsVisibility();
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
            return;

        var key = ApiKeyBox.Password;
        await _plugin.SetApiKeyAsync(key);
        StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
        StatusText.Foreground = Brushes.Gray;
        UpdateSettingsVisibility();
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
                if (models.Count > 0)
                    _plugin.SetFetchedLlmModels(models);

                var voices = await _plugin.FetchVoicesAsync();
                if (voices.Count > 0)
                    _plugin.SetFetchedVoices(voices);

                PopulateControls();
                StatusText.Text = L("Settings.ApiKeyValid");
                StatusText.Foreground = Brushes.Green;
            }
            else
            {
                StatusText.Text = L("Settings.ApiKeyInvalid");
                StatusText.Foreground = Brushes.Red;
            }
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            TestButton.IsEnabled = true;
            UpdateSettingsVisibility();
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        if (TranscriptionModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        if (LlmModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectLlmModel(model.Id);
    }

    private void OnVoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        if (VoicePicker.SelectedItem is PluginVoiceInfo voice)
            _plugin.SelectVoice(voice.Id);
    }

    private void OnCustomVoiceChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        _plugin.SetCustomVoiceId(CustomVoiceBox.Text);
    }

    private void OnLowLatencyChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        _plugin.SetTtsLowLatency(LowLatencyCheckBox.IsChecked.GetValueOrDefault());
    }

    private void OnTextNormalizationChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressControlChanged)
            return;

        _plugin.SetTtsTextNormalization(TextNormalizationCheckBox.IsChecked.GetValueOrDefault());
    }

    private async void OnRefreshModelsClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshModelsButton.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var models = await _plugin.FetchLlmModelsAsync();
            if (models.Count == 0)
            {
                StatusText.Text = L("Settings.ModelsRefreshFailed");
                StatusText.Foreground = Brushes.Orange;
                return;
            }

            _plugin.SetFetchedLlmModels(models);
            PopulateControls();
            StatusText.Text = L("Settings.ModelsFetched", models.Count);
            StatusText.Foreground = Brushes.Green;
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    private async void OnRefreshVoicesClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            StatusText.Text = L("Settings.EnterApiKey");
            StatusText.Foreground = Brushes.Orange;
            return;
        }

        RefreshVoicesButton.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var voices = await _plugin.FetchVoicesAsync();
            if (voices.Count == 0)
            {
                StatusText.Text = L("Settings.VoicesRefreshFailed");
                StatusText.Foreground = Brushes.Orange;
                return;
            }

            _plugin.SetFetchedVoices(voices);
            PopulateControls();
            StatusText.Text = L("Settings.VoicesFetched", voices.Count);
            StatusText.Foreground = Brushes.Green;
        }
        catch (OperationCanceledException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        catch (HttpRequestException ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            RefreshVoicesButton.IsEnabled = true;
        }
    }

    private void PopulateControls()
    {
        _suppressControlChanged = true;
        try
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

            var voices = _plugin.AvailableVoices.ToList();
            VoicePicker.ItemsSource = voices;
            VoicePicker.SelectedItem = voices
                .FirstOrDefault(v => v.Id == _plugin.SelectedVoiceId)
                ?? voices.FirstOrDefault();

            CustomVoiceBox.Text = _plugin.CustomVoiceId;
            LowLatencyCheckBox.IsChecked = _plugin.TtsLowLatency;
            TextNormalizationCheckBox.IsChecked = _plugin.TtsTextNormalization;

            LlmModelHintText.Text = _plugin.FetchedLlmModels.Count > 0
                ? L("Settings.ModelsFetchedHint", _plugin.FetchedLlmModels.Count)
                : L("Settings.LlmModelHint");
            VoiceHintText.Text = _plugin.FetchedVoices.Count > 0
                ? L("Settings.VoicesFetchedHint", _plugin.FetchedVoices.Count)
                : L("Settings.VoiceHint");
        }
        finally
        {
            _suppressControlChanged = false;
        }
    }

    private void UpdateSettingsVisibility()
    {
        SettingsSection.Visibility = _plugin.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;
}
