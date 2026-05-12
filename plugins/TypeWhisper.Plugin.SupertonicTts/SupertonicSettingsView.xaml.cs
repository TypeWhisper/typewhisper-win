using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.SupertonicTts;

public partial class SupertonicSettingsView : UserControl
{
    private readonly SupertonicTtsPlugin _plugin;
    private CancellationTokenSource? _downloadCts;
    private bool _isInitializing = true;
    private bool _isDownloading;

    public SupertonicSettingsView(SupertonicTtsPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        ApplyLocalization();
        LicenseCheckBox.IsChecked = _plugin.HasAcceptedModelLicense;
        SpeedSlider.Value = _plugin.Speed;
        StepsSlider.Value = _plugin.DenoisingSteps;
        _isInitializing = false;
        UpdateSliderText();
        UpdateStatus();
    }

    private void ApplyLocalization()
    {
        TitleText.Text = L("Settings.Title");
        DescriptionText.Text = L("Settings.Description");
        LicenseCheckBox.Content = L("Settings.AcceptLicense");
        DownloadButton.Content = L("Settings.Download");
        SpeedLabel.Text = L("Settings.Speed");
        StepsLabel.Text = L("Settings.Quality");
        SourceText.Text = L("Settings.Source");
        LicenseText.Text = L("Settings.License");
    }

    private void OnLicenseChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
            return;

        _plugin.SetLicenseAccepted(LicenseCheckBox.IsChecked == true);
        UpdateStatus();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCts?.Cancel();
            return;
        }

        _downloadCts = new CancellationTokenSource();
        _isDownloading = true;
        DownloadButton.Content = L("Settings.Downloading");
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        UpdateStatus();

        var progress = new Progress<double>(value =>
        {
            DownloadProgress.Value = Math.Max(0, Math.Min(100, value * 100));
        });

        try
        {
            await _plugin.DownloadAssetsAsync(progress, _downloadCts.Token);
            StatusText.Text = L("Settings.DownloadComplete");
            StatusText.Foreground = Brushes.LightGreen;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L("Settings.DownloadCancelled");
            StatusText.Foreground = Brushes.Orange;
        }
        catch (Exception ex)
        {
            StatusText.Text = L("Settings.Error", ex.Message);
            StatusText.Foreground = Brushes.OrangeRed;
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            _isDownloading = false;
            DownloadButton.Content = L("Settings.Download");
            DownloadProgress.Visibility = Visibility.Collapsed;
            UpdateStatus();
        }
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitializing)
            _plugin.SetSpeed(e.NewValue);
        UpdateSliderText();
    }

    private void OnStepsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitializing)
            _plugin.SetDenoisingSteps((int)Math.Round(e.NewValue));
        UpdateSliderText();
    }

    private void UpdateStatus()
    {
        if (_isDownloading)
        {
            StatusText.Text = L("Settings.Downloading");
            StatusText.Foreground = Brushes.CornflowerBlue;
        }
        else if (_plugin.AreAssetsReady)
        {
            StatusText.Text = L("Settings.Ready");
            StatusText.Foreground = Brushes.LightGreen;
        }
        else
        {
            StatusText.Text = L("Settings.NotReady");
            StatusText.Foreground = Brushes.DarkGray;
        }

        DownloadButton.IsEnabled = !_plugin.AreAssetsReady
            && (_plugin.HasAcceptedModelLicense || _isDownloading);
    }

    private void UpdateSliderText()
    {
        if (SpeedValueText is not null)
            SpeedValueText.Text = $"{SupertonicTtsPlugin.NormalizeSpeed(SpeedSlider.Value):0.00}x";
        if (StepsValueText is not null)
            StepsValueText.Text = SupertonicTtsPlugin.NormalizeDenoisingSteps((int)Math.Round(StepsSlider.Value)).ToString();
    }

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args) => _plugin.Loc?.GetString(key, args) ?? string.Format(key, args);
}
