using System.Windows;

namespace TypeWhisper.Windows.Services;

public interface IAppRestartService
{
    void RestartMinimized();
}

public interface IAppNotificationService
{
    void ShowBalloon(string title, string message, Action? onClick = null);
}

public sealed class AppRestartService : IAppRestartService
{
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
