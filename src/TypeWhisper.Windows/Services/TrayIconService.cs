using System.Drawing;
using System.IO;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides tray icon service behavior.
/// </summary>
public sealed class TrayIconService : IAppNotificationService, IDisposable
{
    private TaskbarIcon? _trayIcon;
    private bool _disposed;
    private Action? _pendingBalloonClick;

    /// <summary>
    /// Raised when show settings requested.
    /// </summary>
    public event EventHandler? ShowSettingsRequested;
    /// <summary>
    /// Raised when show file transcription requested.
    /// </summary>
    public event EventHandler? ShowFileTranscriptionRequested;
    /// <summary>
    /// Raised when show recent transcriptions requested.
    /// </summary>
    public event EventHandler? ShowRecentTranscriptionsRequested;
    /// <summary>
    /// Raised when copy last transcription requested.
    /// </summary>
    public event EventHandler? CopyLastTranscriptionRequested;
    /// <summary>
    /// Raised when read back last transcription requested.
    /// </summary>
    public event EventHandler? ReadBackLastTranscriptionRequested;
    /// <summary>
    /// Raised when update check requested.
    /// </summary>
    public event EventHandler? UpdateCheckRequested;
    /// <summary>
    /// Raised when exit requested.
    /// </summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Initializes resources required before use.
    /// </summary>
    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "TypeWhisper",
            NoLeftClickDelay = true,
        };

        _trayIcon.ContextMenu = BuildContextMenu();
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        _trayIcon.TrayBalloonTipClicked += (_, _) =>
        {
            var action = _pendingBalloonClick;
            _pendingBalloonClick = null;
            action?.Invoke();
        };

        _trayIcon.Icon = LoadIcon();

        _trayIcon.ForceCreate();
    }

    /// <summary>
    /// Updates tooltip.
    /// </summary>
    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
    }

    /// <summary>
    /// Shows balloon.
    /// </summary>
    public void ShowBalloon(string title, string message, Action? onClick = null)
    {
        _pendingBalloonClick = onClick;
        _trayIcon?.ShowNotification(title, message);
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.Settings"] };
        settingsItem.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var fileItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.TranscribeFile"] };
        fileItem.Click += (_, _) => ShowFileTranscriptionRequested?.Invoke(this, EventArgs.Empty);

        var recentItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.RecentTranscriptions"] };
        recentItem.Click += (_, _) => ShowRecentTranscriptionsRequested?.Invoke(this, EventArgs.Empty);

        var copyLastItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.CopyLastTranscription"] };
        copyLastItem.Click += (_, _) => CopyLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);

        var readBackItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.ReadBackLast"] };
        readBackItem.Click += (_, _) => ReadBackLastTranscriptionRequested?.Invoke(this, EventArgs.Empty);

        var updateItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.CheckUpdates"] };
        updateItem.Click += (_, _) => UpdateCheckRequested?.Invoke(this, EventArgs.Empty);

        var separatorItem = new System.Windows.Controls.Separator();

        var exitItem = new System.Windows.Controls.MenuItem { Header = Loc.Instance["Tray.Exit"] };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(settingsItem);
        menu.Items.Add(fileItem);
        menu.Items.Add(recentItem);
        menu.Items.Add(copyLastItem);
        menu.Items.Add(readBackItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(separatorItem);
        menu.Items.Add(exitItem);

        return menu;
    }

    private static Icon LoadIcon()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Icons", "app.ico");
        if (File.Exists(iconPath))
            return new Icon(iconPath, 16, 16);

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0, 120, 212));
        using var font = new Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("T", font, brush, 1f, 0f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>
    /// Releases resources held by the instance.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _trayIcon?.Dispose();
            _disposed = true;
        }
    }
}
