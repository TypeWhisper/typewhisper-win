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
    private readonly MenuFlyoutItem _status;
    private readonly MenuFlyoutItem _recordingAction;
    private readonly MenuFlyoutItem _cancelProcessing;
    private readonly MenuFlyoutItem _exitAction;
    private bool _closing;
    private readonly MenuFlyoutItem _pauseHotkeys;
    private string _dictationStatus = "Loading…";
    private bool _hotkeysPaused;
    private string? _pauseError;

    internal TrayIconService(Action show, Action settings, Action history, Action files, Action exit, Action finishDictation, Action cancelProcessing, Action togglePause, Action recovery, Action updates)
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
        _status = Label("Loading Parakeet…");
        menu.Items.Add(_status);
        menu.Items.Add(new MenuFlyoutSeparator());
        _recordingAction = CreateItem("Start with dictation shortcut", "\uE720", finishDictation);
        _recordingAction.IsEnabled = false;
        menu.Items.Add(_recordingAction);
        _cancelProcessing = CreateItem("Cancel processing", "\uE711", cancelProcessing);
        _cancelProcessing.IsEnabled = false;
        menu.Items.Add(_cancelProcessing);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("General"));
        menu.Items.Add(CreateItem("Quick Launch", "\uE80F", show));
        menu.Items.Add(CreateItem("Settings", "\uE713", settings));
        menu.Items.Add(CreateItem("History", "\uE81C", history));
        menu.Items.Add(Unavailable("Error log", "\uE9CE"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("Transcription"));
        _pauseHotkeys = CreateItem("Pause dictation hotkeys", "\uE769", togglePause);
        _pauseHotkeys.IsEnabled = false;
        menu.Items.Add(_pauseHotkeys);
        menu.Items.Add(CreateItem("Transcribe file…", "\uE8A5", files));
        menu.Items.Add(CreateItem("Review recovery recordings…", "\uE777", recovery));
        var recent = new MenuFlyoutSubItem { Text = "Last transcription", IsEnabled = false, FontSize = 13 };
        recent.Items.Add(Unavailable("Copy", "\uE8C8"));
        recent.Items.Add(Unavailable("Read back", "\uE767"));
        menu.Items.Add(recent);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("Check for updates…", "\uE895", updates));
        menu.Items.Add(new MenuFlyoutSeparator());
        _exitAction = CreateItem("Exit", "\uE7E8", exit);
        menu.Items.Add(_exitAction);

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

#if DEBUG
    internal void PresentProbe()
    {
        if (!_closing && WinUIProfile.IsTestProfile && Environment.GetEnvironmentVariable("TYPEWHISPER_WINUI_TRAY_PROBE") == "1")
            _menuWindow.Present();
    }
#endif

    internal void UpdateDictation(string status, bool recording)
    {
        if (_closing) return;
        _dictationStatus = recording ? "Recording" : status;
        _status.Text = _pauseError ?? (_hotkeysPaused ? "Dictation hotkeys paused. Resume them from the tray menu." : _dictationStatus);
        _recordingAction.Text = recording ? "Finish dictation" : "Start with dictation shortcut";
        _recordingAction.IsEnabled = recording;
        ToolTipService.SetToolTip(_status, _status.Text);
        _icon.ToolTipText = "TypeWhisper · " + _status.Text[..Math.Min(_status.Text.Length, 90)];
    }

    internal void UpdateHotkeyPause(bool paused, bool canChange, string? error)
    {
        if (_closing) return;
        _hotkeysPaused = paused; _pauseError = error;
        _pauseHotkeys.Text = paused ? "Resume dictation hotkeys" : "Pause dictation hotkeys";
        _pauseHotkeys.IsEnabled = canChange;
        _status.Text = error ?? (paused ? "Dictation hotkeys paused. Resume them from the tray menu." : _dictationStatus);
        ToolTipService.SetToolTip(_status, _status.Text);
        _icon.ToolTipText = "TypeWhisper · " + _status.Text[..Math.Min(_status.Text.Length, 90)];
    }

    internal void SetShutdownState(string status)
    {
        _closing = true;
        _menuWindow.DisableActions();
        _status.Text = status;
        _icon.ToolTipText = "TypeWhisper · " + status;
    }

    internal void UpdateProcessing(bool canCancel)
    {
        if (!_closing) _cancelProcessing.IsEnabled = canCancel;
    }
    internal void AllowShutdownRetry() => _exitAction.IsEnabled = true;

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
