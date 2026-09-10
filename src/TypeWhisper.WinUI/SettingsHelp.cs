using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

internal static class SettingsHelp
{
    internal static StackPanel Label(string title, string help, double size = 14)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = title, FontSize = size, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextBrush"]
        });
        if (!string.IsNullOrWhiteSpace(help)) row.Children.Add(Button(title, help));
        return row;
    }

    internal static HandCursorButton Button(string title, string help)
    {
        var button = new HandCursorButton
        {
            Content = new TypeWhisperGlyph { Kind = "info", Width = 16, Height = 16 },
            Width = 28, Height = 28, MinWidth = 28, MinHeight = 28, Padding = new Thickness(6),
            Background = new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, $"About {title}");
        Update(button, help);
        return button;
    }

    internal static void Update(HandCursorButton button, string help)
    {
        button.Visibility = string.IsNullOrWhiteSpace(help) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(button, help);
        TextBlock Copy() => new()
        {
            Text = help, TextWrapping = TextWrapping.Wrap, MaxWidth = 400,
            IsTextSelectionEnabled = true
        };
        if (button.Flyout is Flyout { Content: TextBlock text }) text.Text = help;
        else button.Flyout = new Flyout { Content = Copy() };
        ToolTipService.SetToolTip(button, new ToolTip { Content = Copy() });
    }
}
