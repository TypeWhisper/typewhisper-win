using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.WinUI.Platform;
using global::Windows.Foundation;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

/// <summary>A message shown in the notice card at the overlay position.</summary>
/// <param name="Text">What happened and what to do next.</param>
/// <param name="Title">A short heading; errors default to "TypeWhisper".</param>
/// <param name="IsError">Errors stay longer and use the warning color.</param>
/// <param name="ActionLabel">Label of an optional button, such as "Edit workflow".</param>
/// <param name="Action">What the button does; the card closes first.</param>
/// <param name="Duration">How long the card stays; hovering pauses the countdown.</param>
/// <param name="Actions">Further buttons after the optional one, such as "Later"; primary ones use the accent style.</param>
internal sealed record AppNotice(string Text, string? Title = null, bool IsError = true, string? ActionLabel = null,
    Action? Action = null, TimeSpan? Duration = null, IReadOnlyList<NoticeAction>? Actions = null);

/// <summary>A button on the notice card; the card closes before <paramref name="Run"/> is called.</summary>
internal sealed record NoticeAction(string Label, Action Run, bool Primary = false);

// Replaces the notice area of the former Quick Launch window. Like the dictation overlay, it never takes focus, so
// a shortcut used in another app leaves that app in front.
internal sealed class NoticeWindow : Window
{
    private const int CardWidth = 420;
    private static readonly global::Windows.UI.Color ErrorColor = Microsoft.UI.ColorHelper.FromArgb(255, 255, 159, 10);
    private readonly Border _card;
    private readonly TextBlock _title = new() { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _text = new() { FontSize = 13, TextWrapping = TextWrapping.WrapWholeWords };
    // Long messages, such as the upgrade report, scroll instead of being cut off.
    private readonly ScrollViewer _textScroll = new() { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private bool _announce;
    // Buttons wrap to a second line when translated labels do not fit beside each other.
    private readonly ShortcutWrapPanel _actions = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ScaleTransform _countdown = new() { ScaleX = 1 };
    private readonly Border _countdownBar;
    private readonly Stopwatch _clock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private TimeSpan _duration;
    private TimeSpan _elapsedBeforePause;
    private bool _closed;
    private DisplayArea? _area;
    private OverlayPreferences? _layout;
    private double _offset;

    internal NoticeWindow()
    {
        Title = Loc.T("TypeWhisper Notice");
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = presenter.IsMinimizable = false;
        }

        var close = new HandCursorButton
        {
            Content = new TextBlock { Text = "×", FontSize = 14 }, Padding = new Thickness(6, 0, 6, 2), MinWidth = 0, MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top, Style = (Style)Application.Current.Resources["IconButtonStyle"]
        };
        AutomationProperties.SetName(close, Loc.T("Dismiss notice"));
        close.Click += (_, _) => Dismiss();
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        _text.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Assertive);
        _countdownBar = new Border { Background = (Brush)Application.Current.Resources["AccentBrush"], CornerRadius = new CornerRadius(1.5),
            RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _countdown };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(header);
        _textScroll.Content = _text;
        content.Children.Add(_textScroll);
        content.Children.Add(_actions);
        content.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = (Brush)Application.Current.Resources["HairlineBrush"], Child = _countdownBar });
        _card = new Border
        {
            Background = (Brush)Application.Current.Resources["InkBrush"], BorderBrush = (Brush)Application.Current.Resources["HairlineBrush"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 10, 12), Child = content
        };
        // Hovering pauses the countdown so a longer message can be read.
        _card.PointerEntered += (_, _) => { if (_clock.IsRunning) { _elapsedBeforePause += _clock.Elapsed; _clock.Reset(); } };
        _card.PointerExited += (_, _) => { if (!_closed && AppWindow.IsVisible) _clock.Restart(); };
        _card.VerticalAlignment = VerticalAlignment.Top;
        _card.Loaded += (_, _) => Fit();
        Content = _card;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) =>
        {
            var elapsed = _elapsedBeforePause + _clock.Elapsed;
            var remaining = Math.Clamp(1 - elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
            _countdown.ScaleX = remaining;
            if (remaining == 0) Dismiss();
        };
        NativeWindowAppearance.RemoveOverlayFrame(this);
        Closed += (_, _) => { _closed = true; _timer.Stop(); SystemBackdrop = null; };
    }

    internal bool IsShowing => !_closed && AppWindow.IsVisible;

    /// <summary>Shows or replaces the notice beside the overlay; <paramref name="offset"/> (logical pixels) keeps a visible overlay uncovered.</summary>
    internal void Show(AppNotice notice, DisplayArea area, OverlayPreferences layout, double offset)
    {
        if (_closed) return;
        _title.Text = notice.Title ?? "TypeWhisper";
        _title.Foreground = notice.IsError ? new SolidColorBrush(ErrorColor) : (Brush)Application.Current.Resources["AccentBrush"];
        _countdownBar.Background = notice.IsError ? new SolidColorBrush(ErrorColor) : (Brush)Application.Current.Resources["AccentBrush"];
        _text.Text = notice.Text;
        _textScroll.ChangeView(null, 0, null, true);
        _announce = true;
        _actions.Children.Clear();
        var actions = (notice.Action is not null && notice.ActionLabel is not null ? [new NoticeAction(notice.ActionLabel, notice.Action)] : Array.Empty<NoticeAction>())
            .Concat(notice.Actions ?? []);
        foreach (var action in actions)
        {
            var button = new HandCursorButton
            {
                Content = action.Label,
                Style = (Style)Application.Current.Resources[action.Primary ? "PrimaryButtonStyle" : "SecondaryButtonStyle"]
            };
            button.Click += (_, _) =>
            {
                // A replaced notice must not run the buttons of the one before.
                if (!_actions.Children.Contains(button)) return;
                Dismiss();
                action.Run();
            };
            _actions.Children.Add(button);
        }
        _actions.Visibility = _actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_card, _title.Text + ". " + notice.Text);
        _duration = notice.Duration ?? TimeSpan.FromSeconds(notice.IsError ? 12 : 6);
        _elapsedBeforePause = TimeSpan.Zero;
        _countdown.ScaleX = 1;

        (_area, _layout, _offset) = (area, layout, offset);
        Place(96);
        NativeWindowAppearance.MakeNonActivatingToolWindow(this);
        AppWindow.Show(activateWindow: false);
        NativeWindowAppearance.RemoveOverlayFrame(this);
        _clock.Restart();
        _timer.Start();
        // The card measures its text only once it is part of the shown window.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Fit);
    }

    // The card fills the window, so measure it without a height limit and size the window to fit.
    private void Fit()
    {
        if (!IsShowing) return;
        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        if (_card.DesiredSize.Height > 0) Place(_card.DesiredSize.Height);
        // A live setting alone is not announced; screen readers need the event once the new text is laid out.
        if (_announce && (FrameworkElementAutomationPeer.FromElement(_text) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_text)) is { } peer)
        {
            _announce = false;
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    // The window follows the measured card height, so a longer message is never cut off.
    private void Place(double contentHeight)
    {
        if (_area is not { } area || _layout is not { } layout) return;
        var scale = Math.Max(96u, NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96d;
        var work = area.WorkArea;
        var width = Math.Min((int)Math.Round(CardWidth * scale), work.Width - (int)(32 * scale));
        var height = (int)Math.Ceiling(Math.Max(contentHeight, 56) * scale);
        var side = (int)Math.Round(24 * scale);
        var edge = (int)Math.Round((38 + _offset) * scale);
        var x = layout.HorizontalIndex switch { 0 => work.X + side, 2 => work.X + work.Width - width - side, _ => work.X + (work.Width - width) / 2 };
        var y = layout.AtTop ? work.Y + edge : work.Y + work.Height - height - edge;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    internal void Dismiss()
    {
        _timer.Stop();
        _clock.Reset();
        _actions.Children.Clear();
        if (!_closed) AppWindow.Hide();
    }
}
