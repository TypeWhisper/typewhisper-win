using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.WinUI.Platform;
using global::Windows.Graphics;

namespace TypeWhisper.WinUI;

// Increment only when the curated highlights change, not for every application build.
internal sealed class WhatsNewWindow : Window
{
    internal const int ContentVersion = 1;
    private bool _active;
    private bool _closed;

    internal WhatsNewWindow()
    {
        Title = Loc.T("What's new in TypeWhisper");
        NativeWindowAppearance.ApplyAppTitleBar(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        }

        var shell = new Grid { Background = Resource("InkBrush"), BorderBrush = Resource("HairlineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(28, 24, 28, 24), RowSpacing = 16 };
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var dragRegion = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), MinHeight = 42 };
        dragRegion.Children.Add(new TypeWhisperGlyph { Kind = "signal", Width = 42, Height = 42, HorizontalAlignment = HorizontalAlignment.Left });
        header.Children.Add(dragRegion);
        var close = new HandCursorButton
        {
            Content = "×", FontSize = 22, Width = 36, Height = 36, Padding = new Thickness(0),
            Margin = new Thickness(0, -8, -12, 0), VerticalAlignment = VerticalAlignment.Top,
            Style = (Style)Application.Current.Resources["IconButtonStyle"]
        };
        AutomationProperties.SetName(close, Loc.T("Close"));
        ToolTipService.SetToolTip(close, Loc.T("Close"));
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        shell.Children.Add(header);
        var body = new StackPanel { Spacing = 24 };
        var heading = new StackPanel { Spacing = 10 };
        var title = Copy(Title, 26);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        heading.Children.Add(title);
        heading.Children.Add(Copy(Loc.T("A quick look at the latest improvements. Your settings stay as they are."), 14, muted: true));
        body.Children.Add(heading);
        body.Children.Add(Highlight("search", Loc.T("Find your spoken language faster"),
            Loc.T("Search long selection lists by typing a language name. Open Dictation in Settings to try it.")));
        body.Children.Add(Highlight("layout", Loc.T("An app icon for your overlay"),
            Loc.T("Choose the TypeWhisper app icon as a compact overlay widget in Appearance settings.")));
        body.Children.Add(Highlight("globe", Loc.T("Browser dictation within reach"),
            Loc.T("Find the browser extension in Integrations, alongside Raycast and the command-line tool.")));
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetRow(scroll, 1); shell.Children.Add(scroll);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var skip = new HandCursorButton { Content = Loc.T("Skip"), MinHeight = 36, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
        var done = new HandCursorButton { Content = Loc.T("Got it"), MinHeight = 36, Style = (Style)Application.Current.Resources["PrimaryButtonStyle"] };
        skip.Click += (_, _) => Close();
        done.Click += (_, _) => Close();
        footer.Children.Add(skip); footer.Children.Add(done);
        Grid.SetRow(footer, 2); shell.Children.Add(footer);
        shell.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Escape) return;
            e.Handled = true; Close();
        };
        PageKeyboardNavigation.Attach(shell);
        Content = shell;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(dragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        NativeWindowAppearance.RemoveSystemBorder(this);
        Closed += (_, _) => _closed = true;
        // Set a useful tab stop only after the user activates this window; startup must keep the dictation target.
        void FocusDone() { if (_active && !_closed) done.Focus(FocusState.Programmatic); }
        shell.Loaded += (_, _) => FocusDone();
        Activated += (_, e) =>
        {
            _active = e.WindowActivationState != WindowActivationState.Deactivated;
            if (!_active) return;
            NativeWindowAppearance.RemoveSystemBorder(this);
            DispatcherQueue.TryEnqueue(FocusDone);
        };
    }

    internal void ShowWithoutTakingFocus()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + work.Height / 2));
        var dpi = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var width = Math.Min((int)(560 * scale), Math.Max(1, work.Width - (int)(48 * scale)));
        var height = Math.Min((int)(550 * scale), Math.Max(1, work.Height - (int)(48 * scale)));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        AppWindow.Show(activateWindow: false);
    }

    private static Grid Highlight(string glyph, string title, string description)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TypeWhisperGlyph { Kind = glyph, Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel { Spacing = 5 };
        var heading = Copy(title, 16); heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        text.Children.Add(heading); text.Children.Add(Copy(description, 14, muted: true));
        Grid.SetColumn(text, 1); row.Children.Add(text);
        return row;
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
    private static TextBlock Copy(string text, double size, bool muted = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = Resource(muted ? "MutedBrush" : "TextBrush")
    };
}
