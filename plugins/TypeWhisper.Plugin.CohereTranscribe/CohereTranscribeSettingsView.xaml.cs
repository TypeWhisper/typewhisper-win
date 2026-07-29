using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.CohereTranscribe;

/// <summary>
/// Provides optional Hugging Face authentication settings for local asset downloads.
/// </summary>
public partial class CohereTranscribeSettingsView : UserControl
{
    private readonly CohereTranscribePlugin _plugin;
    private bool _suppressChanges;

    /// <summary>
    /// Initializes a new instance of the CohereTranscribeSettingsView class.
    /// </summary>
    public CohereTranscribeSettingsView(CohereTranscribePlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        TokenLabel.Text = L("Settings.HuggingFaceToken");
        TokenHintText.Text = L("Settings.HuggingFaceTokenHint");
        SaveButton.Content = L("Settings.Save");
        RemoveButton.Content = L("Settings.Remove");

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _suppressChanges = true;
        try
        {
            TokenBox.Password = _plugin.HuggingFaceToken ?? "";
            UpdateRemoveButton();
        }
        finally
        {
            _suppressChanges = false;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressChanges)
            return;

        StatusText.Text = "";
        UpdateRemoveButton();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        try
        {
            await _plugin.SetHuggingFaceTokenAsync(TokenBox.Password);
            StatusText.Text = _plugin.HuggingFaceToken is null
                ? L("Settings.Removed")
                : L("Settings.Saved");
            StatusText.Foreground = Brushes.Green;
        }
        catch (ArgumentException)
        {
            StatusText.Text = L("Settings.InvalidToken");
            StatusText.Foreground = Brushes.Red;
        }
        catch (Exception exception) when (
            CohereTranscribePlugin.IsExpectedSecretStorageFailure(exception))
        {
            StatusText.Text = L("Settings.SaveFailed");
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            SaveButton.IsEnabled = true;
            RemoveButton.IsEnabled = true;
            UpdateRemoveButton();
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        try
        {
            await _plugin.SetHuggingFaceTokenAsync(null);
            _suppressChanges = true;
            TokenBox.Password = "";
            StatusText.Text = L("Settings.Removed");
            StatusText.Foreground = Brushes.Green;
        }
        catch (Exception exception) when (
            CohereTranscribePlugin.IsExpectedSecretStorageFailure(exception))
        {
            StatusText.Text = L("Settings.SaveFailed");
            StatusText.Foreground = Brushes.Red;
        }
        finally
        {
            _suppressChanges = false;
            SaveButton.IsEnabled = true;
            RemoveButton.IsEnabled = true;
            UpdateRemoveButton();
        }
    }

    private void UpdateRemoveButton()
    {
        RemoveButton.Visibility =
            !string.IsNullOrWhiteSpace(TokenBox.Password) || _plugin.HuggingFaceToken is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
}
