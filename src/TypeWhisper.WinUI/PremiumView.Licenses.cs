using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

internal sealed partial class PremiumView
{
    private readonly PasswordBox _licenseKey = new() { PlaceholderText = Loc.T("License key"), MaxLength = 512, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _activateLicense = new HandCursorButton { Style = (Style)Application.Current.Resources["PrimaryButtonStyle"], Content = Loc.T("Activate"), MinWidth = 96 };
    private readonly TextBlock _licenseNotice = Copy("", 12, true);
    private readonly StackPanel _licenseStatuses = new() { Spacing = 12 };
    private readonly Grid _licenseInput = new() { ColumnSpacing = 10 };
    private readonly StackPanel _licenseLinks = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    // Shown instead of key entry and purchase links while machine policy provides the commercial key.
    private readonly StackPanel _managedLicense = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _managedLicenseError = Copy("", 12, true);
    private readonly Button _retryManagedLicense = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Retry activation"), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _confirmingDeactivation;

    private UIElement CreateLicenseSection()
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(SettingsHelp.Label(Loc.T("License"), Loc.T("Activate a commercial or supporter key from your purchase email. License credentials are encrypted for your Windows user and saved in this app profile. An activation uses a device slot; deactivate this device to release it."), 16));
        _managedLicense.Children.Add(Copy(Loc.T("Your organization provides the commercial license on this device. Activation happens automatically and the key is not shown."), 12, true));
        AutomationProperties.SetLiveSetting(_managedLicenseError, AutomationLiveSetting.Polite);
        _managedLicense.Children.Add(_managedLicenseError);
        _retryManagedLicense.Click += async (_, _) => await WinUILicensing.RetryManagedAsync();
        _managedLicense.Children.Add(_retryManagedLicense);
        panel.Children.Add(_managedLicense);
        panel.Children.Add(_licenseStatuses);
        _licenseInput.ColumnDefinitions.Add(new()); _licenseInput.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        AutomationProperties.SetName(_licenseKey, Loc.T("License key"));
        _licenseKey.PasswordChanged += (_, _) => UpdateLicenseInput();
        _licenseKey.KeyDown += async (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Enter || !_activateLicense.IsEnabled) return;
            e.Handled = true; await ActivateLicenseAsync();
        };
        _activateLicense.Click += async (_, _) => await ActivateLicenseAsync();
        _licenseInput.Children.Add(_licenseKey); Grid.SetColumn(_activateLicense, 1); _licenseInput.Children.Add(_activateLicense);
        panel.Children.Add(_licenseInput);
        AutomationProperties.SetLiveSetting(_licenseNotice, AutomationLiveSetting.Polite);
        panel.Children.Add(_licenseNotice);
        _licenseLinks.Children.Add(new HyperlinkButton { Content = Loc.T("Manage purchases"), NavigateUri = new Uri("https://polar.sh/typewhisper/portal") });
        panel.Children.Add(_licenseLinks);
        return Card(panel);
    }

    private void UpdateLicenseInput()
    {
        _activateLicense.IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation && !string.IsNullOrWhiteSpace(_licenseKey.Password);
        _licenseKey.IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation;
        _activateLicense.Content = WinUILicensing.Busy ? Loc.T("Please wait…") : Loc.T("Activate");
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
        var service = WinUILicensing.Service;
        var managed = WinUILicensing.Managed;
        _managedLicense.Visibility = managed ? Visibility.Visible : Visibility.Collapsed;
        _licenseInput.Visibility = managed ? Visibility.Collapsed : Visibility.Visible;
        _licenseLinks.Visibility = managed ? Visibility.Collapsed : Visibility.Visible;
        _managedLicenseError.Text = managed && service.ManagedLicenseFailed
            ? Loc.T("The organization license could not be activated. Check the network connection and contact your administrator if this continues.") : "";
        _managedLicenseError.Visibility = string.IsNullOrEmpty(_managedLicenseError.Text) ? Visibility.Collapsed : Visibility.Visible;
        _retryManagedLicense.IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation;
        _retryManagedLicense.Content = WinUILicensing.Busy ? Loc.T("Please wait…") : Loc.T("Retry activation");
        _licenseNotice.Text = service.StorageError ?? WinUILicensing.Notice ?? (WinUILicensing.Busy ? Loc.T("Checking license…") : "");
        _licenseNotice.Visibility = string.IsNullOrEmpty(_licenseNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
        _licenseStatuses.Children.Clear();
        AddLicenseStatus(true, Loc.T("Commercial license"), service.CommercialStatus, service.CommercialTierDisplayName, service.HasCommercialActivation, managed);
        AddLicenseStatus(false, Loc.T("Supporter"), service.SupporterStatus, service.SupporterTierDisplayName, service.HasSupporterActivation, false);
    }

    private void AddLicenseStatus(bool commercial, string title, LicenseStatus status, string? tier, bool activated, bool managed)
    {
        if (!activated) return;
        var panel = new StackPanel { Spacing = 8 };
        var heading = title + (tier is null ? "" : " · " + tier) + " · " + (status switch { LicenseStatus.Active => Loc.T("Active"), LicenseStatus.Expired => Loc.T("Expired"), _ => Loc.T("Unlicensed") });
        if (managed) heading += " · " + Loc.T("Managed by your organization");
        panel.Children.Add(Copy(heading, 14));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var refresh = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Refresh status"), IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation };
        AutomationProperties.SetName(refresh, Loc.T("Refresh {0}", title));
        refresh.Click += async (_, _) => await WinUILicensing.RefreshAsync(commercial);
        actions.Children.Add(refresh);
        // A managed activation is released by removing the policy value or centrally in Polar, not per device.
        if (!managed)
        {
            var deactivate = new HandCursorButton { Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], Content = Loc.T("Deactivate this device"), IsEnabled = !WinUILicensing.Busy && !_confirmingDeactivation };
            AutomationProperties.SetName(deactivate, Loc.T("Deactivate {0} on this device", title));
            deactivate.Click += async (_, _) =>
            {
                if (_confirmingDeactivation || WinUILicensing.Busy) return;
                _confirmingDeactivation = true; RefreshLicenseSection();
                try
                {
                    if (await Dialogs.ConfirmAsync(this, Loc.T("Deactivate {0}?", title),
                        Loc.T("This releases this device's activation. Your subscription or purchase is not cancelled."),
                        Loc.T("Deactivate"), destructive: true, closeText: Loc.T("Keep active"))) await WinUILicensing.DeactivateAsync(commercial);
                }
                finally { _confirmingDeactivation = false; RefreshLicenseSection(); }
            };
            actions.Children.Add(deactivate);
        }
        panel.Children.Add(actions);
        _licenseStatuses.Children.Add(panel);
    }
}
