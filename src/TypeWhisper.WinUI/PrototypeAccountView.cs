using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// In-memory UI scenarios. Never reads licenses, accepts keys or contacts an updater.
internal sealed class PrototypeAccountView : UserControl
{
    internal PrototypeAccountView(Dictionary<string, string> values, List<PrototypeChoicePicker> pickers)
    {
        var body = new StackPanel { Spacing = 22 };
        Content = body;
        var identity = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(new PrototypeSetupLogo { HorizontalAlignment = HorizontalAlignment.Center });
        identity.Children.Add(Copy("TypeWhisper", 26, center: true));
        identity.Children.Add(Copy("Windows · WinUI prototype", 13, true, true));
        identity.Children.Add(Copy("Speak naturally. Keep your flow.", 14, true, true));
        body.Children.Add(identity);

        var license = new StackPanel { Spacing = 14 };
        license.Children.Add(Heading("lock", "License"));
        var licenseTitle = Copy("", 18);
        var licenseHint = Copy("", 13, true);
        license.Children.Add(licenseTitle); license.Children.Add(licenseHint);
        var licensePicker = new PrototypeChoicePicker();
        licensePicker.Configure("Preview license state", "lock", "Preview license state");
        licensePicker.SetOptions([
            new("inactive", "Not activated", "No license has been connected in this demo."),
            new("active", "Licensed", "Preview an activated device."),
            new("unavailable", "Verification unavailable", "Preview a connection problem without changing activation.")
        ], values.GetValueOrDefault("PreviewAccount.License", "inactive"));
        void ShowLicense(string id)
        {
            values["PreviewAccount.License"] = id;
            (licenseTitle.Text, licenseHint.Text) = id switch
            {
                "active" => ("Licensed · example", "This PC · sample activation. Your real license has not been read or changed."),
                "unavailable" => ("Unable to verify · example", "Check your connection and try again. This demo does not determine your actual license status."),
                _ => ("No license connected", "Activation and purchase management will be connected later. Do not enter a real license key into this prototype.")
            };
        }
        licensePicker.SelectionChanged += ShowLicense;
        ShowLicense(licensePicker.SelectedId); pickers.Add(licensePicker);
        license.Children.Add(Copy("Demo state", 12, true)); license.Children.Add(licensePicker);
        body.Children.Add(Card(license));

        var updates = new StackPanel { Spacing = 14 };
        updates.Children.Add(Heading("restore", "Updates"));
        var channel = new PrototypeChoicePicker();
        channel.Configure("Update channel", "settings", "Preview update channel");
        channel.SetOptions([
            new("stable", "Stable", "Regular releases for everyday use."),
            new("preview", "Preview", "Early changes that may still have rough edges.")
        ], values.GetValueOrDefault("PreviewAccount.Channel", "stable"));
        var updateStatus = Copy("No update check has been run. This is an isolated development build.", 13, true);
        channel.SelectionChanged += id => { values["PreviewAccount.Channel"] = id; updateStatus.Text = "Channel selected for this session only. No update settings were changed."; };
        pickers.Add(channel); updates.Children.Add(Copy("Update channel", 13)); updates.Children.Add(channel);
        var outcome = new PrototypeChoicePicker();
        outcome.Configure("Preview check result", "info", "Preview update check result");
        outcome.SetOptions([
            new("current", "Up to date", "Show a successful check with no new release."),
            new("available", "Update available", "Show a fictional release, without downloading anything."),
            new("offline", "Connection unavailable", "Show a failed check and allow another attempt.")
        ], "current");
        pickers.Add(outcome); updates.Children.Add(Copy("Simulated check result", 12, true)); updates.Children.Add(outcome);
        var check = Button("Simulate update check", () => updateStatus.Text = outcome.SelectedId switch
        {
            "available" => "Update available · example only. Downloads and installation are not connected.",
            "offline" => "Could not check for updates · simulated connection error. You can try again.",
            _ => "Up to date · simulated result, not a check against published releases."
        });
        updates.Children.Add(check); updates.Children.Add(updateStatus); body.Children.Add(Card(updates));
        body.Children.Add(Copy("About this build", 16));
        body.Children.Add(Copy("An isolated Windows interface preview inspired by TypeWhisper for Mac. Settings are kept for this session only.", 13, true));
        body.Children.Add(Copy("Preview only · no purchases, license activation, network requests or installations.", 12, true));
    }

    private static StackPanel Heading(string icon, string title)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TypeWhisperGlyph { Kind = icon, Width = 22, Height = 22 });
        row.Children.Add(Copy(title, 16)); return row;
    }
    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static TextBlock Copy(string text, double size, bool muted = false, bool center = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        Foreground = Brush(muted ? "MutedBrush" : "TextBrush"),
        FontWeight = size >= 16 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
    private static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(18), CornerRadius = new CornerRadius(10), Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
    private static HandCursorButton Button(string label, Action action)
    {
        var button = new HandCursorButton { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        button.Click += (_, _) => action(); return button;
    }
}
