using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

// This window never creates profile stores; failed startup recovery must stay read-only.
internal sealed class ProfileOperationWindow : Window
{
    private readonly TextBlock _message;
    private readonly HandCursorButton _close;
    private readonly HandCursorButton _retry;
    private readonly Expander _details;
    private readonly TextBlock _diagnostic;
    private bool _busy;
    private Func<Task>? _retryAction;

    internal ProfileOperationWindow(string message, bool busy, Action exit)
    {
        Title = "TypeWhisper · Profile restore";
        var body = new StackPanel { Spacing = 18, Padding = new(24), Background = (Brush)Application.Current.Resources["InkBrush"] };
        body.Children.Add(new TextBlock { Text = "Profile restore", FontSize = 24, Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextBrush"] };
        body.Children.Add(_message);
        _diagnostic = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _details = new Expander { Header = "Technical details", Content = _diagnostic, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(_details);
        _retry = new HandCursorButton { Content = "Retry saving and restoring", Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        _retry.Click += async (_, _) =>
        {
            var retry = _retryAction;
            _retryAction = null; _retry.Visibility = Visibility.Collapsed;
            if (retry is not null) await retry();
        };
        body.Children.Add(_retry);
        _close = new HandCursorButton { Content = "Close TypeWhisper", HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["PrototypeSecondaryButtonStyle"] };
        _close.Click += (_, _) => exit(); body.Children.Add(_close);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        NativeWindowAppearance.ApplyAppTitleBar(this);
        AppWindow.Resize(new SizeInt32(620, 300));
        AppWindow.Closing += (_, args) => { args.Cancel = true; if (!_busy) exit(); };
        SetMessage(message, busy);
    }

    internal void SetMessage(string message, bool busy)
    { _message.Text = message; _busy = busy; _close.IsEnabled = !busy; _retry.IsEnabled = !busy; }

    internal void SetDetails(string? details)
    { _diagnostic.Text = details ?? ""; _details.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible; }

    internal void OfferSaveRetry(Func<Task> retry)
    {
        _retryAction = retry;
        _retry.Visibility = Visibility.Visible;
    }
}
