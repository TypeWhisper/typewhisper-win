using Microsoft.UI.Dispatching;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private DispatcherQueueTimer? _accountRefreshTimer;

    // Modules accept a signed account envelope only for a while after the server issued it, so a running app fetches a
    // new one once a day. An hourly check also catches up after sleep and retries after a failed refresh.
    private void StartDailyAccountRefresh()
    {
        _accountRefreshTimer = DispatcherQueue.CreateTimer();
        _accountRefreshTimer.Interval = TimeSpan.FromHours(1);
        _accountRefreshTimer.IsRepeating = true;
        _accountRefreshTimer.Tick += (_, _) =>
        {
            if (_closing) return;
            // Without the startup refresh, as after a failed start, the saved session is not loaded yet.
            WinUIPremiumAccount.LoadSavedSession();
            if (WinUIPremiumAccount.RefreshDue) _ = WinUIPremiumAccount.RefreshAsync();
        };
        _accountRefreshTimer.Start();
    }

    private void StopDailyAccountRefresh() { _accountRefreshTimer?.Stop(); _accountRefreshTimer = null; }
}
