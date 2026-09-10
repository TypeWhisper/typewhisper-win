using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed partial class MarketplaceView
{
    private string? _bulkError;
    private void UpdateAllAction()
    {
        if (_runtime is null) return;
        var updates = _runtime.Packages.Updates;
        var count = updates.Available.Count;
        MarketUpdateAllButton.Visibility = !IsDetail && (count > 0 || updates.RestartRequired || updates.Busy)
            ? Visibility.Visible : Visibility.Collapsed;
        MarketUpdateAllButton.Content = updates.Busy ? "Updating..." : count > 0 ? $"Update all ({count})" : "Restart now";
        MarketUpdateAllButton.IsEnabled = !updates.Busy && !_restarting && _installation is null && _runtime.CanChangeProvider;
        UpdateNotice.Text = _bulkError ?? updates.Status ?? "";
        UpdateNotice.Visibility = !IsDetail && (_bulkError ?? updates.Status) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MarketUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !MarketUpdateAllButton.IsEnabled) return;
        _bulkError = null;
        if (_runtime.Packages.Updates.Available.Count > 0) await _runtime.Packages.Updates.UpdateAsync();
        else if (RestartRequested is not null)
        {
            _restarting = true; UpdateAllAction();
            try { _bulkError = await RestartRequested(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _bulkError = "Restart could not finish. Close and reopen TypeWhisper."; }
            finally { _restarting = false; UpdateAllAction(); }
        }
        Filter(_query);
    }
}
