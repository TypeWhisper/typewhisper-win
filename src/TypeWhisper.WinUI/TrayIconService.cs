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
    private string _dictationStatus = Loc.T("Loading…");
    private bool _hotkeysPaused;
    private string? _pauseError;
    private Action? _notificationClicked;
    // While the Recorder captures audio: its sources for the tooltip, and the marked icon shown in the tray.
    private string? _recorderStatus;
    private bool _markShown;
    private System.Drawing.Icon? _shownIcon;

    internal TrayIconService(Action toggleRecorder, Action recent, Action settings, Action history, Action files, Action exit, Action finishDictation, Action cancelProcessing, Action togglePause, Action recovery, Action updates,
        Action pasteLast, Action copyLast, Action readLast, Action diagnostics)
    {
        var menu = new MenuFlyout();
        var presenterStyle = new Style(typeof(MenuFlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, Light ? ElementTheme.Light : ElementTheme.Dark));
        presenterStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Light ? Rgb(249, 249, 249) : Rgb(43, 43, 43))));
        presenterStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Light ? Rgb(209, 209, 214) : Rgb(69, 69, 69))));
        presenterStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 4, 0, 4)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 230d));
        menu.MenuFlyoutPresenterStyle = presenterStyle;
        _status = Label(Loc.T("Loading Parakeet…"));
        menu.Items.Add(_status);
        menu.Items.Add(new MenuFlyoutSeparator());
        // The recorder toggle is the primary action, as on macOS. Dictation actions appear only while they apply.
        _recorderAction = CreateItem(Loc.T("Start recording"), "\uE7C8", toggleRecorder);
        menu.Items.Add(_recorderAction);
        _recordingAction = CreateItem(Loc.T("Finish dictation"), "\uE720", finishDictation);
        _recordingAction.Visibility = Visibility.Collapsed;
        menu.Items.Add(_recordingAction);
        _cancelProcessing = CreateItem(Loc.T("Cancel processing"), "\uE711", cancelProcessing);
        _cancelProcessing.Visibility = Visibility.Collapsed;
        menu.Items.Add(_cancelProcessing);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label(Loc.T("General")));
        menu.Items.Add(CreateItem(Loc.T("Settings\u2026"), "\uE713", settings));
        menu.Items.Add(CreateItem(Loc.T("History"), "\uE81C", history));
        menu.Items.Add(CreateItem(Loc.T("Diagnostics"), "\uE9D9", diagnostics));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Label(Loc.T("Transcription")));
        _pauseHotkeys = CreateItem(Loc.T("Pause dictation hotkeys"), "\uE769", togglePause);
        _pauseHotkeys.IsEnabled = false;
        menu.Items.Add(_pauseHotkeys);
        menu.Items.Add(CreateItem(Loc.T("Transcribe file…"), "\uE8A5", files));
        menu.Items.Add(CreateItem(Loc.T("Review recovery recordings…"), "\uE777", recovery));
        // Flat items like the macOS menu; a submenu cannot open beside the tray menu's own window.
        // Paste targets the app window used before the tray menu opened.
        menu.Items.Add(CreateItem(Loc.T("Recent transcriptions"), "\uE81C", recent));
        menu.Items.Add(CreateItem(Loc.T("Paste last transcription"), "\uE77F", pasteLast));
        menu.Items.Add(CreateItem(Loc.T("Copy last transcription"), "\uE8C8", copyLast));
        menu.Items.Add(CreateItem(Loc.T("Read back last transcription"), "\uE767", readLast));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateItem(Loc.T("Check for updates…"), "\uE895", updates));
        menu.Items.Add(new MenuFlyoutSeparator());
        _exitAction = CreateItem(Loc.T("Exit"), "\uE7E8", exit);
        menu.Items.Add(_exitAction);

        _menuWindow = new TrayMenuWindow(menu);
        _icon = new TaskbarIcon
        {
            ToolTipText = WinUIProfile.DisplayName,
            IconSource = new BitmapImage(new Uri("ms-appx:///app.ico")),
            RightClickCommand = new TrayCommand(_menuWindow.Present),
            LeftClickCommand = new TrayCommand(settings),
            NoLeftClickDelay = true,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayIcon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            if (e.MouseEvent == H.NotifyIcon.Core.MouseEvent.BalloonToolTipClicked && !_closing) _notificationClicked?.Invoke();
        };
    }

    public void Dispose()
    {
        _icon.Dispose();
        _shownIcon?.Dispose();
        _menuWindow.Close();
    }

    internal void UpdateDictation(string status, bool recording)
    {
        if (_closing) return;
        _dictationStatus = recording ? Loc.T("Recording") : status;
        _status.Text = _pauseError ?? (_hotkeysPaused ? Loc.T("Dictation hotkeys paused. Resume them from the tray menu.") : _dictationStatus);
        _recordingAction.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_status, _status.Text);
        RefreshToolTip();
    }

    internal void UpdateHotkeyPause(bool paused, bool canChange, string? error)
    {
        if (_closing) return;
        _hotkeysPaused = paused; _pauseError = error;
        _pauseHotkeys.Text = paused ? Loc.T("Resume dictation hotkeys") : Loc.T("Pause dictation hotkeys");
        _pauseHotkeys.IsEnabled = canChange;
        _status.Text = error ?? (paused ? Loc.T("Dictation hotkeys paused. Resume them from the tray menu.") : _dictationStatus);
        ToolTipService.SetToolTip(_status, _status.Text);
        RefreshToolTip();
    }

    // The Recorder's sources come first, so a long dictation status never cuts them off. Windows shows at most 127
    // characters.
    private void RefreshToolTip()
    {
        var text = _recorderStatus is { } recorder ? recorder + " · " + _status.Text : _status.Text;
        _icon.ToolTipText = WinUIProfile.DisplayName + " · " + text[..Math.Min(text.Length, 100)];
    }

    internal void SetShutdownState(string status)
    {
        _closing = true;
        _menuWindow.DisableActions();
        _status.Text = status;
        _icon.ToolTipText = WinUIProfile.DisplayName + " · " + status;
    }

    internal void UpdateProcessing(bool canCancel)
    {
        if (!_closing) _cancelProcessing.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void UpdateRecorder(bool recording, bool canToggle, bool microphone = false, bool systemAudio = false)
    {
        if (_closing) return;
        _recorderAction.Text = recording ? Loc.T("Stop recording") : Loc.T("Start recording");
        ((FontIcon)_recorderAction.Icon).Glyph = recording ? "" : "";
        _recorderAction.IsEnabled = canToggle;
        var status = recording ? RecorderIndicator.Status(microphone, systemAudio) : null;
        // A mark that could not be shown is tried again on the next update, even when the status did not change.
        if (status == _recorderStatus && _markShown == recording) return;
        _recorderStatus = status;
        if (_markShown != recording) ShowRecordingMark(recording);
        RefreshToolTip();
    }

    // A red record button instead of the app icon while the Recorder captures audio, as a lasting sign next to the
    // notification, which Do Not Disturb may hold back.
    private void ShowRecordingMark(bool recording)
    {
        try
        {
            var icon = CreateTrayIcon(recording);
            if (!_icon.UpdateIcon(icon))
            {
                icon.Dispose();
                AppDiagnostics.Write("tray.recording-mark.not-updated");
                return;
            }
            _shownIcon?.Dispose();
            _shownIcon = icon;
            _markShown = recording;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The tooltip still names the recording.
            AppDiagnostics.Write("tray.recording-mark.failed", ex);
        }
    }

    private static System.Drawing.Icon CreateTrayIcon(bool recording)
    {
        var size = Math.Max(16, GetSystemMetricsForDpi(SmallIconWidth, GetDpiForSystem()));
        using var source = new System.Drawing.Icon(Path.Join(AppContext.BaseDirectory, "app.ico"), size, size);
        if (!recording) return (System.Drawing.Icon)source.Clone();
        using var bitmap = source.ToBitmap();
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            // A corner dot was too small to notice in the tray, so the whole icon becomes a record button.
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);
            var diameter = bitmap.Width - 1f;
            var ring = Math.Max(1f, bitmap.Width / 10f);
            var x = 0.5f;
            var y = 0.5f;
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(232, 17, 35));
            graphics.FillEllipse(white, x, y, diameter, diameter);
            graphics.FillEllipse(red, x + ring, y + ring, diameter - 2 * ring, diameter - 2 * ring);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private const int SmallIconWidth = 49;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    internal void AllowShutdownRetry() => _exitAction.IsEnabled = true;

    // A Windows notification from the tray icon, shown at the bottom right like in TypeWhisper 1.0.
    // Windows keeps it quiet during Do Not Disturb; clicking it runs the latest action.
    internal void ShowNotification(string title, string message, Action clicked)
    {
        if (_closing) return;
        _notificationClicked = clicked;
        _icon.ShowNotification(title, message);
    }

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
        Foreground = new SolidColorBrush(Light ? Rgb(29, 29, 31) : Rgb(244, 247, 250)),
        Icon = new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Width = 16,
            Height = 16,
            Foreground = new SolidColorBrush(Light ? Rgb(0, 102, 204) : Rgb(10, 132, 255)),
        },
    };

    // The menu follows the app theme, which stays fixed for the lifetime of the process.
    internal static bool Light => Application.Current.RequestedTheme == ApplicationTheme.Light;

    private static global::Windows.UI.Color Rgb(byte r, byte g, byte b) => Microsoft.UI.ColorHelper.FromArgb(255, r, g, b);

    private sealed class TrayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
