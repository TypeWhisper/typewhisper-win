using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.Soniox;

/// <summary>
/// Provides soniox settings view behavior.
/// </summary>
public partial class SonioxSettingsView : UserControl
{
    private static readonly TimeSpan ApiKeySaveDebounce = TimeSpan.FromMilliseconds(300);

    private readonly SonioxPlugin _plugin;
    private CancellationTokenSource? _saveDebounceCts;
    private readonly bool _suppressPasswordChanged;
    private bool _suppressRegionChanged;

    /// <summary>
    /// Initializes a new instance of the SonioxSettingsView class.
    /// </summary>
    public SonioxSettingsView(SonioxPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();

        ApiKeyLabel.Text = L("Settings.ApiKeyLabel");
        TestButton.Content = L("Settings.Test");
        RegionLabel.Text = L("Settings.Region");

        _suppressRegionChanged = true;
        foreach (var region in SonioxPlugin.AvailableRegions)
            RegionBox.Items.Add(new ComboBoxItem { Content = region.DisplayName, Tag = region.Id });
        SelectRegionItem(plugin.RegionId);
        _suppressRegionChanged = false;

        if (!string.IsNullOrEmpty(plugin.ApiKey))
        {
            _suppressPasswordChanged = true;
            ApiKeyBox.Password = plugin.ApiKey;
            _suppressPasswordChanged = false;
        }
    }

    private async void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordChanged)
            return;

        _saveDebounceCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;

        try
        {
            await Task.Delay(ApiKeySaveDebounce, cts.Token);
            var key = ApiKeyBox.Password;
            await _plugin.SetApiKeyAsync(key);
            if (!IsCurrentSave(cts))
                return;

            StatusText.Text = string.IsNullOrWhiteSpace(key) ? "" : L("Settings.Saved");
            StatusText.Foreground = Brushes.Gray;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer password edit superseded this pending save.
        }
        catch (InvalidOperationException ex)
        {
            if (IsCurrentSave(cts))
                ShowError(ex);
        }
        catch (IOException ex)
        {
            if (IsCurrentSave(cts))
                ShowError(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            if (IsCurrentSave(cts))
                ShowError(ex);
        }
        catch (SystemException ex)
        {
            if (IsCurrentSave(cts))
                ShowError(ex);
        }
        catch (ApplicationException ex)
        {
            if (IsCurrentSave(cts))
                ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_saveDebounceCts, cts))
                _saveDebounceCts = null;
        }
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
        RegionBox.IsEnabled = false;
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var detectedRegion = await _plugin.DetectRegionAsync(key);
            if (detectedRegion is not null)
            {
                ApplyDetectedRegion(detectedRegion);
                var displayName = SonioxPlugin.AvailableRegions
                    .First(region => region.Id == detectedRegion).DisplayName;
                StatusText.Text = L("Settings.ApiKeyValidRegion", displayName);
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
            ShowError(ex);
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex);
        }
        catch (SystemException ex)
        {
            ShowError(ex);
        }
        catch (ApplicationException ex)
        {
            ShowError(ex);
        }
        finally
        {
            TestButton.IsEnabled = true;
            RegionBox.IsEnabled = true;
        }
    }

    private void OnRegionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRegionChanged)
            return;

        if ((RegionBox.SelectedItem as ComboBoxItem)?.Tag is string regionId)
            _plugin.SetRegion(regionId);
    }

    private void SelectRegionItem(string regionId)
    {
        foreach (var item in RegionBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag as string == regionId)
            {
                RegionBox.SelectedItem = comboItem;
                return;
            }
        }

        if (RegionBox.Items.Count > 0)
            RegionBox.SelectedIndex = 0;
    }

    private void ApplyDetectedRegion(string regionId)
    {
        _suppressRegionChanged = true;
        SelectRegionItem(regionId);
        _suppressRegionChanged = false;
        _plugin.SetRegion(regionId);
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = L("Settings.Error", ex.Message);
        StatusText.Foreground = Brushes.Red;
    }

    private bool IsCurrentSave(CancellationTokenSource cts) =>
        ReferenceEquals(_saveDebounceCts, cts)
        && !cts.IsCancellationRequested;

    private string L(string key) => L(key, []);

    private string L(string key, params object[] args)
    {
        var localized = _plugin.Loc?.GetString(key, args);
        if (!string.IsNullOrWhiteSpace(localized) && localized != key)
            return localized;

        return FormatFallbackText(key, args);
    }

    internal static string FormatFallbackText(string key, object[] args)
    {
        var text = key switch
        {
            "Settings.ApiKeyLabel" => "API Key",
            "Settings.Test" => "Test",
            "Settings.Saved" => "Saved",
            "Settings.EnterApiKey" => "Enter an API key.",
            "Settings.Testing" => "Testing...",
            "Settings.ApiKeyValid" => "API key valid.",
            "Settings.ApiKeyValidRegion" => "API key valid (region: {0}).",
            "Settings.ApiKeyInvalid" => "Invalid API key.",
            "Settings.Region" => "Soniox region",
            "Settings.Error" => "Error",
            _ => key
        };

        if (args.Length == 0)
            return text;

        return text.Contains('{', StringComparison.Ordinal)
            ? string.Format(text, args)
            : $"{text}: {string.Join(", ", args)}";
    }
}
