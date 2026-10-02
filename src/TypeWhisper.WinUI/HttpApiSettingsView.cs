using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using global::Windows.ApplicationModel.DataTransfer;

namespace TypeWhisper.WinUI;

internal sealed class HttpApiSettingsView : UserControl
{
    internal HttpApiSettingsView(WinUIHttpApi api)
    {
        var body = new SettingsRows();
        var enabled = AppToggleSwitch.Create(api.Enabled);
        AutomationProperties.SetName(enabled, Loc.T("Enable HTTP API"));
        var enable = new SettingsRow().Set(Loc.T("Enable HTTP API"),
            Loc.T("Lets local apps and scripts, such as Raycast and the typewhisper command, control TypeWhisper. Only apps on this PC can connect."), "", enabled);
        body.Children.Add(enable);
        var port = new NumberBox { Value = api.Port, Minimum = 1024, Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 160 };
        AutomationProperties.SetName(port, Loc.T("Port"));
        var portRow = new SettingsRow().Set(Loc.T("Port"), control: port);
        body.Children.Add(portRow);
        var requireAuthentication = AppToggleSwitch.Create(api.RequireAuthentication);
        AutomationProperties.SetName(requireAuthentication, Loc.T("Require API token"));
        var authenticationRow = new SettingsRow().Set(Loc.T("Require API token"),
            Loc.T("Leave off for the existing Raycast extension. Local apps can then connect without a token."), requireAuthentication);
        body.Children.Add(authenticationRow);
        var documentation = new HyperlinkButton { Content = Loc.T("Open documentation"), VerticalAlignment = VerticalAlignment.Center };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        HandCursorButton Button(string title, bool primary = false) => new()
        {
            Content = title, Style = (Style)Application.Current.Resources[primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"]
        };
        var copyAddress = Button(Loc.T("Copy address"));
        var copyToken = Button(Loc.T("Copy API token"));
        var apply = Button(Loc.T("Apply"), true);
        footer.Children.Add(apply); footer.Children.Add(copyAddress); footer.Children.Add(copyToken); footer.Children.Add(documentation);
        var actions = new SettingsRow().Below(footer);
        body.Children.Add(actions);
        var updating = false;
        void Refresh()
        {
            // The switch already says that the API is off.
            enable.Status = api.Enabled || (api.Status != "HTTP API is off." && api.Status != Loc.T("HTTP API is off.")) ? Loc.T(api.Status) : "";
            portRow.Visibility = authenticationRow.Visibility = actions.Visibility = api.Enabled ? Visibility.Visible : Visibility.Collapsed;
            copyAddress.IsEnabled = copyToken.IsEnabled = documentation.IsEnabled = api.Running;
            documentation.NavigateUri = api.Running ? new Uri($"http://127.0.0.1:{api.Port}/docs") : null;
        }
        async Task ConfigureAsync(bool isEnabled, int configuredPort, bool authenticationRequired)
        {
            updating = true;
            apply.IsEnabled = enabled.IsEnabled = port.IsEnabled = requireAuthentication.IsEnabled = false;
            try { await api.ConfigureAsync(isEnabled, configuredPort, authenticationRequired); }
            finally
            {
                enabled.IsOn = api.Enabled;
                port.Value = api.Port;
                requireAuthentication.IsOn = api.RequireAuthentication;
                apply.IsEnabled = enabled.IsEnabled = port.IsEnabled = requireAuthentication.IsEnabled = true;
                updating = false;
                Refresh();
            }
        }
        enabled.Toggled += async (_, _) =>
        {
            if (updating) return;
            await ConfigureAsync(enabled.IsOn, api.Port, api.RequireAuthentication);
        };
        apply.Click += async (_, _) =>
        {
            if (double.IsNaN(port.Value) || port.Value != Math.Truncate(port.Value)) { enable.Status = Loc.T("Enter a whole port number."); return; }
            await ConfigureAsync(enabled.IsOn, (int)port.Value, requireAuthentication.IsOn);
        };
        void Copy(string? value)
        {
            if (value is null) return;
            try { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); enable.Status = Loc.T("Copied."); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { enable.Status = Loc.T("Could not copy. Try again."); }
        }
        copyAddress.Click += (_, _) => Copy($"http://127.0.0.1:{api.Port}");
        copyToken.Click += (_, _) => Copy(api.TokenForCopy);
        Loaded += (_, _) => { api.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => api.Changed -= Refresh;
        Refresh();
        Content = body;
    }
}
