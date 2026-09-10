using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.Windows.Services;

namespace TypeWhisper.WinUI;

internal sealed partial class PremiumView
{
    private readonly PasswordBox _licenseKey = new() { PlaceholderText = "License key", MaxLength = 512, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _activateLicense = new HandCursorButton { Content = "Activate", MinWidth = 96 };
    private readonly TextBlock _licenseNotice = Copy("", 12, true);
    private readonly StackPanel _licenseStatuses = new() { Spacing = 12 };
    private bool _confirmingDeactivation;

    private UIElement CreateLicenseSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsHelp.Label("License", "Activate a commercial or supporter key from your purchase email. License credentials are encrypted for your Windows user and saved in this app profile. An activation uses a device slot; deactivate this device to release it.", 16));
        panel.Children.Add(_licenseStatuses);
        var input = new Grid { ColumnSpacing = 10 };
        input.ColumnDefinitions.Add(new()); input.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetName(_licenseKey, "License key");
        _licenseKey.PasswordChanged += (_, _) => UpdateLicenseInput();
        _licenseKey.KeyDown += async (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter || !_activateLicense.IsEnabled) return;
            e.Handled = true; await ActivateLicenseAsync();
        };
        _activateLicense.Click += async (_, _) => await ActivateLicenseAsync();
        input.Children.Add(_licenseKey); Grid.SetColumn(_activateLicense, 1); input.Children.Add(_activateLicense);
        panel.Children.Add(input);
        AutomationProperties.SetLiveSetting(_licenseNotice, AutomationLiveSetting.Polite);
        panel.Children.Add(_licenseNotice);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        links.Children.Add(new HyperlinkButton { Content = "Manage purchases", NavigateUri = new Uri("https://polar.sh/typewhisper/portal") });
        panel.Children.Add(links);
        return Card(panel);
    }

    private void UpdateLicenseInput()
    {
        _activateLicense.IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation && !string.IsNullOrWhiteSpace(_licenseKey.Password);
        _licenseKey.IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation;
        _activateLicense.Content = WinUILicensing.Busy ? "Please wait…" : "Activate";
    }

    private async Task ActivateLicenseAsync()
    {
        if (!_activateLicense.IsEnabled) return;
        var key = _licenseKey.Password.Trim();
        await WinUILicensing.ActivateAsync(key);
        if (WinUILicensing.Service.LicenseActivationError is null && WinUILicensing.Service.StorageError is null) _licenseKey.Password = "";
        UpdateLicenseInput();
    }

    private void RefreshLicenseSection()
    {
        UpdateLicenseInput();
        _licenseNotice.Text = WinUILicensing.Service.StorageError ?? WinUILicensing.Notice ?? (WinUILicensing.Busy ? "Checking license…" : "");
        _licenseNotice.Visibility = string.IsNullOrEmpty(_licenseNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
        _licenseStatuses.Children.Clear();
        var service = WinUILicensing.Service;
        AddLicenseStatus(true, "Commercial license", service.CommercialStatus, service.CommercialTierDisplayName, service.HasCommercialActivation);
        AddLicenseStatus(false, "Supporter", service.SupporterStatus, service.SupporterTierDisplayName, service.HasSupporterActivation);
    }

    private void AddLicenseStatus(bool commercial, string title, LicenseStatus status, string? tier, bool activated)
    {
        if (!activated) return;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Copy(title + (tier is null ? "" : " · " + tier) + " · " + status, 14));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refresh = new HandCursorButton { Content = "Refresh status", IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation };
        var deactivate = new HandCursorButton { Content = "Deactivate this device", IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation };
        AutomationProperties.SetName(refresh, "Refresh " + title);
        AutomationProperties.SetName(deactivate, "Deactivate " + title + " on this device");
        refresh.Click += async (_, _) => await WinUILicensing.RefreshAsync(commercial);
        deactivate.Click += async (_, _) =>
        {
            if (_confirmingDeactivation || WinUILicensing.Busy) return;
            _confirmingDeactivation = true; RefreshLicenseSection();
            try
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Deactivate " + title + "?",
                    Content = "This releases this device's activation. Your subscription or purchase is not cancelled.",
                    PrimaryButtonText = "Deactivate", CloseButtonText = "Keep active", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await WinUILicensing.DeactivateAsync(commercial);
            }
            finally { _confirmingDeactivation = false; RefreshLicenseSection(); }
        };
        actions.Children.Add(refresh); actions.Children.Add(deactivate); panel.Children.Add(actions);
        _licenseStatuses.Children.Add(panel);
    }
}
