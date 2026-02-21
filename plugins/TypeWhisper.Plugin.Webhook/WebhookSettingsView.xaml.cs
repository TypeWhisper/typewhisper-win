using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.Webhook;

public partial class WebhookSettingsView : UserControl
{
    private readonly WebhookPlugin _plugin;
    private bool _initializing = true;

    public WebhookSettingsView(WebhookPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        // Pre-fill controls from current settings
        UrlBox.Text = plugin.WebhookUrl;
        if (!string.IsNullOrEmpty(plugin.Secret))
            SecretBox.Password = plugin.Secret;

        RecordingStartedCheck.IsChecked = plugin.SendRecordingStarted;
        RecordingStoppedCheck.IsChecked = plugin.SendRecordingStopped;
        TranscriptionCompletedCheck.IsChecked = plugin.SendTranscriptionCompleted;
        TranscriptionFailedCheck.IsChecked = plugin.SendTranscriptionFailed;
        TextInsertedCheck.IsChecked = plugin.SendTextInserted;

        _initializing = false;
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        _plugin.WebhookUrl = UrlBox.Text;
        ShowSaved();
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plugin.Secret = SecretBox.Password;
        ShowSaved();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plugin.SendRecordingStarted = RecordingStartedCheck.IsChecked == true;
        _plugin.SendRecordingStopped = RecordingStoppedCheck.IsChecked == true;
        _plugin.SendTranscriptionCompleted = TranscriptionCompletedCheck.IsChecked == true;
        _plugin.SendTranscriptionFailed = TranscriptionFailedCheck.IsChecked == true;
        _plugin.SendTextInserted = TextInsertedCheck.IsChecked == true;
        ShowSaved();
    }

    private void ShowSaved()
    {
        StatusText.Text = "Gespeichert";
        StatusText.Foreground = Brushes.Gray;
    }
}
