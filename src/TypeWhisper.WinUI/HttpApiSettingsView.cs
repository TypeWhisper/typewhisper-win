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
        var body = new StackPanel { Spacing = 12 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Text("HTTP API", 18);
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        var enabled = AppToggleSwitch.Create(api.Enabled);
        AutomationProperties.SetName(enabled, "Enable HTTP API");
        Grid.SetColumn(enabled, 1);
        header.Children.Add(enabled);
        body.Children.Add(header);

        var status = Text(api.Status, 13);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        var details = new StackPanel { Spacing = 12 };
        body.Children.Add(details);
        var port = new NumberBox { Value = api.Port, Minimum = 1024, Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Header = "Port", Width = 200,
            HorizontalAlignment = HorizontalAlignment.Left };
        details.Children.Add(port);
        var authenticationRow = new Grid();
        authenticationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        authenticationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var authenticationLabel = Text("Require API token", 14);
        authenticationLabel.VerticalAlignment = VerticalAlignment.Center;
        authenticationRow.Children.Add(authenticationLabel);
        var requireAuthentication = AppToggleSwitch.Create(api.RequireAuthentication);
        AutomationProperties.SetName(requireAuthentication, "Require API token");
        Grid.SetColumn(requireAuthentication, 1);
        authenticationRow.Children.Add(requireAuthentication);
        details.Children.Add(authenticationRow);
        details.Children.Add(Text("Leave off for the existing Raycast extension. Local apps can then connect without a token.", 13));
        var documentation = new HyperlinkButton { Content = "Open documentation", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0) };
        details.Children.Add(documentation);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        HandCursorButton Button(string title, bool primary = false) => new()
        {
            Content = title, Style = (Style)Application.Current.Resources[primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"]
        };
        var copyAddress = Button("Copy address");
        var copyToken = Button("Copy API token");
        var apply = Button("Apply", true);
        footer.Children.Add(copyAddress); footer.Children.Add(copyToken); footer.Children.Add(apply);
        details.Children.Add(footer);
        var updating = false;
        void Refresh()
        {
            status.Text = api.Status;
            status.Visibility = api.Enabled || api.Status != "HTTP API is off." ? Visibility.Visible : Visibility.Collapsed;
            details.Visibility = api.Enabled ? Visibility.Visible : Visibility.Collapsed;
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
            if (double.IsNaN(port.Value) || port.Value != Math.Truncate(port.Value)) { status.Text = "Enter a whole port number."; return; }
            await ConfigureAsync(enabled.IsOn, (int)port.Value, requireAuthentication.IsOn);
        };
        void Copy(string? value)
        {
            if (value is null) return;
            try { var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data); status.Text = "Copied."; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = "Could not copy. Try again."; }
        }
        copyAddress.Click += (_, _) => Copy($"http://127.0.0.1:{api.Port}");
        copyToken.Click += (_, _) => Copy(api.TokenForCopy);
        Loaded += (_, _) => { api.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => api.Changed -= Refresh;
        Refresh();
        Content = body;
    }
    private static TextBlock Text(string value, double size) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["TextBrush"]
    };
}
