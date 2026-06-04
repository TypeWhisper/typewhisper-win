using System.Windows;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Defines the app restart service contract.
/// </summary>
public interface IAppRestartService
{
    /// <summary>
    /// Requests an application restart that relaunches TypeWhisper minimized.
    /// </summary>
    void RestartMinimized();
}

/// <summary>
/// Defines the app notification service contract.
/// </summary>
public interface IAppNotificationService
{
    /// <summary>
    /// Shows a tray notification and optionally invokes a callback when the notification is clicked.
    /// </summary>
    void ShowBalloon(string title, string message, Action? onClick = null);
}

/// <summary>
/// Provides app restart service behavior.
/// </summary>
public sealed class AppRestartService : IAppRestartService
{
    /// <summary>
    /// Performs restart minimized.
    /// </summary>
    public void RestartMinimized()
    {
        TypeWhisper.Windows.Program.RequestRestart("--minimized");

        var app = Application.Current;
        if (app is null)
            return;

        if (app.Dispatcher.CheckAccess())
        {
            app.Shutdown();
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(app.Shutdown));
    }
}
