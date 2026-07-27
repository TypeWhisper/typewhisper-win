using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

/// <summary>
/// Provides eleven labs settings view behavior.
/// </summary>
public partial class ElevenLabsSettingsView : UserControl
{
    private readonly ElevenLabsPlugin _plugin;
    private bool _suppressPasswordChanged;
    private bool _isInitializing;

    /// <summary>
    /// Initializes a new instance of the ElevenLabsSettingsView class.
    /// </summary>
    public ElevenLabsSettingsView(ElevenLabsPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ApiKeyLabel.Text = L("Settings.ApiKey");
        TestButton.Content = L("Settings.Test");
        ApiKeyHintText.Text = L("Settings.ApiKeyHint");
        TranscriptionModelLabel.Text = L("Settings.TranscriptionModel");
        ModelHintText.Text = L("Settings.ModelHint");
        TranscriptionModeLabel.Text = L("Settings.TranscriptionMode");
        TranscriptionModeHintText.Text = L("Settings.TranscriptionModeHint");
        TagAudioEventsCheckBox.Content = L("Settings.TagAudioEvents");
        NoVerbatimCheckBox.Content = L("Settings.NoVerbatim");
        TranscriptCleanupHintText.Text = L("Settings.TranscriptCleanupHint");
        SpeakerCountLabel.Text = L("Settings.SpeakerCount");
        RestOnlyOptionsHintText.Text = L("Settings.RestOnlyOptionsHint");
        UseDictionaryTermsCheckBox.Content = L("Settings.UseDictionaryTerms");
        DictionaryTermsHintText.Text = L("Settings.DictionaryTermsHint");

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
        _isInitializing = true;
        PopulateModelPicker();
        PopulateTranscriptionModePicker();
        PopulateSpeakerCountPicker();
        TagAudioEventsCheckBox.IsChecked = _plugin.TagAudioEvents;
        NoVerbatimCheckBox.IsChecked = _plugin.NoVerbatim;
        UseDictionaryTermsCheckBox.IsChecked = _plugin.UseDictionaryTerms;
        _isInitializing = false;
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
            return;

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
        }
    }

    private void OnTranscriptionModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing && TranscriptionModelPicker.SelectedItem is PluginModelInfo model)
            _plugin.SelectModel(model.Id);
    }

    private void OnTranscriptionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing
            && TranscriptionModePicker.SelectedItem is TranscriptionModeOption option)
        {
            _plugin.SetTranscriptionMode(option.Mode);
        }
    }

    private void OnTagAudioEventsChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
            _plugin.SetTagAudioEvents(TagAudioEventsCheckBox.IsChecked == true);
    }

    private void OnNoVerbatimChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
            _plugin.SetNoVerbatim(NoVerbatimCheckBox.IsChecked == true);
    }

    private void OnSpeakerCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitializing && SpeakerCountPicker.SelectedItem is SpeakerCountOption option)
            _plugin.SetSpeakerCount(option.Count);
    }

    private void OnUseDictionaryTermsChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitializing)
            _plugin.SetUseDictionaryTerms(UseDictionaryTermsCheckBox.IsChecked == true);
    }

    private void PopulateModelPicker()
    {
        var models = _plugin.TranscriptionModels.ToList();
        TranscriptionModelPicker.ItemsSource = models;
        TranscriptionModelPicker.SelectedItem = models
            .FirstOrDefault(m => m.Id == _plugin.SelectedModelId)
            ?? models.FirstOrDefault();
    }

    private void PopulateTranscriptionModePicker()
    {
        var options = new List<TranscriptionModeOption>
        {
            new(ElevenLabsTranscriptionMode.Automatic, L("Settings.ModeAutomatic")),
            new(ElevenLabsTranscriptionMode.RestOnly, L("Settings.ModeRestOnly"))
        };

        TranscriptionModePicker.ItemsSource = options;
        TranscriptionModePicker.SelectedItem = options.First(option => option.Mode == _plugin.TranscriptionMode);
    }

    private void PopulateSpeakerCountPicker()
    {
        var options = new List<SpeakerCountOption>
        {
            new(ElevenLabsPlugin.AutomaticSpeakerCount, L("Settings.SpeakerAutomatic"))
        };
        options.AddRange(Enumerable.Range(1, 32).Select(count => new SpeakerCountOption(count, count.ToString())));

        SpeakerCountPicker.ItemsSource = options;
        SpeakerCountPicker.SelectedItem = options.First(option => option.Count == _plugin.SpeakerCount);
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? key;

    private sealed record TranscriptionModeOption(
        ElevenLabsTranscriptionMode Mode,
        string DisplayName);

    private sealed record SpeakerCountOption(
        int Count,
        string DisplayName);
}
