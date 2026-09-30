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
    internal nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(_menuWindow);
    private readonly MenuFlyoutItem _status;
    private readonly MenuFlyoutItem _recorderAction;
    private readonly MenuFlyoutItem _recordingAction;
    private readonly MenuFlyoutItem _cancelProcessing;
    private readonly MenuFlyoutItem _exitAction;
    private bool _closing;
    private readonly MenuFlyoutItem _pauseHotkeys;
    private string _dictationStatus = "Loading…";
    private bool _hotkeysPaused;
    private string? _pauseError;

    internal TrayIconService(Action toggleRecorder, Action recent, Action settings, Action history, Action files, Action exit, Action finishDictation, Action cancelProcessing, Action togglePause, Action recovery, Action updates,
        Action pasteLast, Action copyLast, Action readLast, Action diagnostics)
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
        // The recorder toggle is the primary action, as on macOS. Dictation actions appear only while they apply.
        _recorderAction = CreateItem("Start recording", "\uE7C8", toggleRecorder);
        menu.Items.Add(_recorderAction);
        _recordingAction = CreateItem("Finish dictation", "\uE720", finishDictation);
        _recordingAction.Visibility = Visibility.Collapsed;
        menu.Items.Add(_recordingAction);
        _cancelProcessing = CreateItem("Cancel processing", "\uE711", cancelProcessing);
        _cancelProcessing.Visibility = Visibility.Collapsed;
        menu.Items.Add(_cancelProcessing);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("General"));
        menu.Items.Add(CreateItem("Settings\u2026", "\uE713", settings));
        menu.Items.Add(CreateItem("History", "\uE81C", history));
        menu.Items.Add(CreateItem("Diagnostics", "\uE9D9", diagnostics));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label("Transcription"));
        _pauseHotkeys = CreateItem("Pause dictation hotkeys", "\uE769", togglePause);
        _pauseHotkeys.IsEnabled = false;
        menu.Items.Add(_pauseHotkeys);
        menu.Items.Add(CreateItem("Transcribe file…", "\uE8A5", files));
        menu.Items.Add(CreateItem("Review recovery recordings…", "\uE777", recovery));
        // Flat items like the macOS menu; a submenu cannot open beside the tray menu's own window.
        // Paste targets the app window used before the tray menu opened.
        menu.Items.Add(CreateItem("Recent transcriptions", "\uE81C", recent));
        menu.Items.Add(CreateItem("Paste last transcription", "\uE77F", pasteLast));
        menu.Items.Add(CreateItem("Copy last transcription", "\uE8C8", copyLast));
        menu.Items.Add(CreateItem("Read back last transcription", "\uE767", readLast));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem("Check for updates…", "\uE895", updates));
        menu.Items.Add(new MenuFlyoutSeparator());
        _exitAction = CreateItem("Exit", "\uE7E8", exit);
        menu.Items.Add(_exitAction);

        _menuWindow = new TrayMenuWindow(menu);
        _icon = new TaskbarIcon
        {
            ToolTipText = "TypeWhisper",
            IconSource = new BitmapImage(new Uri("ms-appx:///app.ico")),
            RightClickCommand = new TrayCommand(_menuWindow.Present),
            LeftClickCommand = new TrayCommand(settings),
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void Dispose()
    {
        _icon.Dispose();
        _menuWindow.Close();
    }

    internal void UpdateDictation(string status, bool recording)
    {
        if (_closing) return;
        _dictationStatus = recording ? "Recording" : status;
        _status.Text = _pauseError ?? (_hotkeysPaused ? "Dictation hotkeys paused. Resume them from the tray menu." : _dictationStatus);
        _recordingAction.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
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
        if (!_closing) _cancelProcessing.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateRecorder(bool recording, bool canToggle)
    {
        if (_closing) return;
        _recorderAction.Text = recording ? "Stop recording" : "Start recording";
        ((FontIcon)_recorderAction.Icon).Glyph = recording ? "" : "";
        _recorderAction.IsEnabled = canToggle;
    }
    internal void AllowShutdownRetry() => _exitAction.IsEnabled = true;

    private static MenuFlyoutItem Label(string text) => new()
    {
        Text = text, IsEnabled = false, FontSize = 12,
        FontFamily = (FontFamily)Application.Current.Resources["InterfaceFont"],
    };

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
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 10, 132, 255)),
        },
    };

    private sealed class TrayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
