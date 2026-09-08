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
        var body = new StackPanel { Spacing = 16 };
        var heading = Text("HTTP API", 18); body.Children.Add(heading);
        body.Children.Add(Text("Connect local scripts and apps to TypeWhisper. Requests use the model selected in Dictation.", 14));
        var enabled = AppToggleSwitch.Create(api.Enabled);
        AutomationProperties.SetName(enabled, "Enable HTTP API");
        body.Children.Add(Text("Enable HTTP API", 14)); body.Children.Add(enabled);
        var port = new NumberBox { Value = api.Port, Minimum = 1024, Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Header = "Port", Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(port);
        var status = Text(api.Status, 13);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        body.Children.Add(status);
        body.Children.Add(Text("Only connections from this computer are accepted. Every endpoint except status requires the API token. Browser-origin requests are blocked.", 13));
        body.Children.Add(Text("Auto-discovery: api-discovery.json and api-port in this profile. The discovery token is readable only by your Windows user.", 13));
        body.Children.Add(Text("Available: status, models, capabilities and file transcription (upload or local path). JSON, text and provider-timed subtitles are supported.", 13));
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        HandCursorButton Button(string title, bool primary = false) => new()
        {
            Content = title, Style = (Style)Application.Current.Resources[primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"]
        };
        var copyAddress = Button("Copy address");
        var copyToken = Button("Copy API token");
        var apply = Button("Apply", true);
        footer.Children.Add(copyAddress); footer.Children.Add(copyToken); footer.Children.Add(apply);
        body.Children.Add(footer);
        void Refresh()
        {
            status.Text = api.Status;
            copyAddress.IsEnabled = copyToken.IsEnabled = api.Running;
        }
        apply.Click += async (_, _) =>
        {
            if (double.IsNaN(port.Value) || port.Value != Math.Truncate(port.Value)) { status.Text = "Enter a whole port number."; return; }
            apply.IsEnabled = enabled.IsEnabled = port.IsEnabled = false;
            try { await api.ConfigureAsync(enabled.IsOn, (int)port.Value); Refresh(); }
            finally { apply.IsEnabled = enabled.IsEnabled = port.IsEnabled = true; }
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
