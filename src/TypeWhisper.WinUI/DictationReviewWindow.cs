using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

internal sealed class DictationReviewWindow : Window
{
    internal DictationReviewWindow(DictationOutputResult result)
    {
        Title = "Review dictation · TypeWhisper";
        AppWindow.Resize(new SizeInt32(680, 460));
        var body = new Grid { Padding = new Thickness(24), RowSpacing = 16,
            Background = (Brush)Application.Current.Resources["InkBrush"] };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = "Review dictation", FontSize = 24,
            Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        var status = new TextBlock { Text = result.Message + (result.Saved ? "" : " Closing this window discards this review copy."),
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        heading.Children.Add(status);
        body.Children.Add(heading);
        var transcript = new TextBox { AcceptsReturn = true, IsReadOnly = true, Text = result.Record.FinalText.ReplaceLineEndings("\r"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"] };
        AutomationProperties.SetName(transcript, "Dictation result");
        ScrollViewer.SetVerticalScrollBarVisibility(transcript, ScrollBarVisibility.Auto);
        Grid.SetRow(transcript, 1); body.Children.Add(transcript);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new HandCursorButton { Content = "Copy text", Style = (Style)Application.Current.Resources["PrototypePrimaryButtonStyle"] };
        copy.Click += (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(result.Record.FinalText);
                Clipboard.SetContent(data);
                status.Text = "Copied. " + (result.Saved ? "Saved to History." : "Not saved to History.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { status.Text = "Clipboard unavailable. Your text is still here; try again."; }
        };
        var close = new HandCursorButton { Content = "Done", Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        close.Click += (_, _) => Close();
        actions.Children.Add(copy); actions.Children.Add(close);
        Grid.SetRow(actions, 2); body.Children.Add(actions);
        Content = body;
    }
}
