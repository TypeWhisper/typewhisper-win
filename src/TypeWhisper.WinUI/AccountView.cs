using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace TypeWhisper.WinUI;

// About information reflects this build; Premium owns access state.
internal sealed class AccountView : UserControl
{
    internal AccountView(Dictionary<string, string> values, List<ChoicePicker> pickers)
    {
        var body = new StackPanel { Spacing = 22 };
        Content = body;
        var identity = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        identity.Children.Add(new SetupLogo { HorizontalAlignment = HorizontalAlignment.Center });
        identity.Children.Add(Copy("TypeWhisper", 26, center: true));
        identity.Children.Add(Copy("Windows", 13, true, true));
        identity.Children.Add(Copy("Speak naturally. Keep your flow.", 14, true, true));
        body.Children.Add(identity);

        body.Children.Add(Copy("Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "Unknown"), 13, true));
        body.Children.Add(Copy("Premium and licenses", 16));
        body.Children.Add(Copy("Activate and manage your license under Premium in the sidebar.", 13, true));
        body.Children.Add(Copy("App updates", 16));
        body.Children.Add(Copy("Automatic app updates are not connected in this build. Plugin updates are available in Integrations.", 13, true));
    }

    private static Brush Brush(string name) => (Brush)Application.Current.Resources[name];
    private static TextBlock Copy(string text, double size, bool muted = false, bool center = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        Foreground = Brush(muted ? "MutedBrush" : "TextBrush"),
        FontWeight = size >= 16 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
    };
}
