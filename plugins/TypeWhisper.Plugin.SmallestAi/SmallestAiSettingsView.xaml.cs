using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TypeWhisper.Plugin.SmallestAi;

/// <summary>
/// Provides smallest ai settings view behavior.
/// </summary>
public partial class SmallestAiSettingsView : UserControl
{
    private static readonly TimeSpan ApiKeySaveDebounce = TimeSpan.FromMilliseconds(300);

    private readonly SmallestAiPlugin _plugin;
    private CancellationTokenSource? _saveDebounceCts;
    private readonly bool _suppressPasswordChanged;

    /// <summary>
    /// Initializes a new instance of the SmallestAiSettingsView class.
    /// </summary>
    public SmallestAiSettingsView(SmallestAiPlugin plugin)
    {
        _plugin = plugin;
        InitializeComponent();
        ApiKeyLabel.Text = L("Settings.ApiKeyLabel");
        TestButton.Content = L("Settings.Test");

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
        StatusText.Text = L("Settings.Testing");
        StatusText.Foreground = Brushes.Gray;

        try
        {
            var valid = await _plugin.ValidateApiKeyAsync(key);
            if (valid)
            {
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
        }
    }

    private void ShowError(Exception ex)
    {
        StatusText.Text = L("Settings.Error", ex.Message);
        StatusText.Foreground = Brushes.Red;
    }

    private bool IsCurrentSave(CancellationTokenSource cts) =>
        ReferenceEquals(_saveDebounceCts, cts)
        && !cts.IsCancellationRequested;

    private string L(string key) => _plugin.Loc?.GetString(key) ?? key;
    private string L(string key, params object[] args)
    {
        var localized = _plugin.Loc?.GetString(key, args);
        if (localized is not null)
            return localized;

        if (args.Length == 0)
            return key;

        return FormatFallbackText(key, args);
    }

    internal static string FormatFallbackText(string key, object[] args) =>
        args.Length == 0
            ? key
            : key.Contains('{')
                ? string.Format(key, args)
                : $"{key}: {string.Join(", ", args)}";
}
