using System.Windows.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace TypeWhisper.WinUI;

// Host integration only: commands are supplied by the application lifetime owner.
internal sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly TrayMenuWindow _menuWindow;

    internal TrayIconService(Action show, Action settings, Action history, Action files, Action exit)
    {
        var menu = new MenuFlyout();
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ElementTheme.Dark));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 43, 43, 43))));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 69, 69, 69))));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 230d));
        menu.MenuFlyoutPresenterStyle = presenterStyle;
        menu.Items.Add(Label("UI preview · engine not connected"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Unavailable("Start recording", "\uE720"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("General"));
        menu.Items.Add(CreateItem("Quick Launch", "\uE80F", show));
        menu.Items.Add(CreateItem("Settings", "\uE713", settings));
        menu.Items.Add(CreateItem("History", "\uE81C", history));
        menu.Items.Add(Unavailable("Error log", "\uE9CE"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("Transcription"));
        menu.Items.Add(Unavailable("Pause dictation hotkeys", "\uE769"));
        menu.Items.Add(CreateItem("Transcribe file…", "\uE8A5", files));
        menu.Items.Add(Unavailable("Recover last recording", "\uE777"));
        var recent = new MenuFlyoutSubItem { Text = "Last transcription", IsEnabled = false, FontSize = 13 };
        recent.Items.Add(Unavailable("Copy", "\uE8C8"));
        recent.Items.Add(Unavailable("Read back", "\uE767"));
        menu.Items.Add(recent);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("Integrations"));
        menu.Items.Add(Label("Plugin actions not connected yet"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Unavailable("Check for updates…", "\uE895"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("Exit", "\uE7E8", exit));

        _menuWindow = new TrayMenuWindow(menu);
        _icon = new TaskbarIcon
        {
            ToolTipText = "TypeWhisper · WinUI development",
            IconSource = new BitmapImage(new Uri("ms-appx:///app.ico")),
            RightClickCommand = new TrayCommand(_menuWindow.Present),
            LeftClickCommand = new TrayCommand(show),
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        _icon.Dispose();
        _menuWindow.Close();
    }

    private static MenuFlyoutItem Label(string text) => new()
    {
        Text = text, IsEnabled = false, FontSize = 12,
        FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"],
    };

    private static MenuFlyoutItem Unavailable(string text, string glyph)
    {
        var item = CreateItem(text, glyph, () => { });
        item.IsEnabled = false;
        ToolTipService.SetToolTip(item, "Not connected in this UI migration yet.");
        return item;
    }

    private static MenuFlyoutItem CreateItem(string text, string glyph, Action action) => new()
    {
        Text = text,
        Command = new TrayCommand(action),
        FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"],
        FontSize = 13,
        Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 244, 247, 250)),
        Icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Width = 16,
            Height = 16,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 59, 167, 255)),
        },
    };

    private sealed class TrayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
