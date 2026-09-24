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
    private readonly HandCursorButton _alternate;
    private readonly Expander _details;
    private readonly TextBlock _diagnostic;
    private bool _busy;
    private bool _dismissed;
    private Func<Task>? _retryAction;
    private Func<Task>? _alternateAction;

    internal ProfileOperationWindow(string message, bool busy, Action exit, string heading = "Profile restore")
    {
        Title = "TypeWhisper · " + heading;
        var body = new StackPanel { Spacing = 18, Padding = new(24), Background = (Brush)Application.Current.Resources["InkBrush"] };
        body.Children.Add(new TextBlock { Text = heading, FontSize = 24, Foreground = (Brush)Application.Current.Resources["TextBrush"] });
        _message = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextBrush"] };
        body.Children.Add(_message);
        _diagnostic = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
        _details = new Expander { Header = "Technical details", Content = _diagnostic, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(_details);
        _retry = new HandCursorButton { Content = "Retry saving and restoring", Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        _retry.Click += async (_, _) => await RunAction(_retryAction);
        _alternate = new HandCursorButton { Visibility = Visibility.Collapsed,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        _alternate.Click += async (_, _) => await RunAction(_alternateAction);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _retry, _alternate } });
        _close = new HandCursorButton { Content = "Close TypeWhisper", HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        _close.Click += (_, _) => exit(); body.Children.Add(_close);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        NativeWindowAppearance.ApplyAppTitleBar(this);
        AppWindow.Resize(new SizeInt32(620, 300));
        AppWindow.Closing += (_, args) => { if (_dismissed) return; args.Cancel = true; if (!_busy) exit(); };
        SetMessage(message, busy);
    }

    internal void SetMessage(string message, bool busy)
    { _message.Text = message; _busy = busy; _close.IsEnabled = !busy; _retry.IsEnabled = !busy; _alternate.IsEnabled = !busy; }

    internal void Dismiss() { _dismissed = true; Close(); }

    internal void SetDetails(string? details)
    { _diagnostic.Text = details ?? ""; _details.Visibility = details is null ? Visibility.Collapsed : Visibility.Visible; }

    internal void OfferSaveRetry(Func<Task> retry) => OfferActions("Retry saving and restoring", retry);

    // Each offer is single-use; the action re-offers on another failure.
    internal void OfferActions(string retryLabel, Func<Task> retry, string? alternateLabel = null, Func<Task>? alternate = null)
    {
        _retry.Content = retryLabel; _retryAction = retry; _retry.Visibility = Visibility.Visible;
        _alternate.Content = alternateLabel; _alternateAction = alternate;
        _alternate.Visibility = alternate is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAction(Func<Task>? action)
    {
        _retryAction = _alternateAction = null;
        _retry.Visibility = _alternate.Visibility = Visibility.Collapsed;
        if (action is not null) await action();
    }
}
