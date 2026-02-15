using System.Drawing;
using System.IO;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace TypeWhisper.Windows.Services;

public sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private bool _disposed;

    public event EventHandler? ShowSettingsRequested;
    public event EventHandler? ShowFileTranscriptionRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "TypeWhisper",
            NoLeftClickDelay = true,
        };

        _trayIcon.ContextMenu = BuildContextMenu();
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        _trayIcon.Icon = LoadIcon();

        _trayIcon.ForceCreate();
    }

    public void UpdateTooltip(string text)
    {
        if (_trayIcon is not null)
            _trayIcon.ToolTipText = text;
    }

    public void ShowBalloon(string title, string message)
    {
        _trayIcon?.ShowNotification(title, message);
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Einstellungen..." };
        settingsItem.Click += (_, _) => ShowSettingsRequested?.Invoke(this, EventArgs.Empty);

        var fileItem = new System.Windows.Controls.MenuItem { Header = "Datei transkribieren..." };
        fileItem.Click += (_, _) => ShowFileTranscriptionRequested?.Invoke(this, EventArgs.Empty);

        var separatorItem = new System.Windows.Controls.Separator();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Beenden" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.Add(settingsItem);
        menu.Items.Add(fileItem);
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
        g.Clear(Color.FromArgb(88, 101, 242));
        using var font = new Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        g.DrawString("T", font, brush, 1f, 0f);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _trayIcon?.Dispose();
            _disposed = true;
        }
    }
}
