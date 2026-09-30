using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed partial class PluginsView
{
    internal Func<Task<string?>>? RestartRequested { get; set; }
    private bool _updating;
    private string? _updateError;
    private string? RuntimeUpdateStatus(string? id) => id is null || _runtime is null ? null
        : _runtime.Packages.Store.PendingRestart(id) ? "Restart required"
        : _runtime.Packages.Updates.HasUpdate(id) ? "Update available" : null;

    private void UpdateUpdateAction()
    {
        if (_runtime is null) return;
        var updates = _runtime.Packages.Updates;
        var count = updates.Available.Count;
        InstalledUpdateButton.Visibility = count > 0 || updates.RestartRequired || updates.Busy || _updating
            ? Visibility.Visible : Visibility.Collapsed;
        InstalledUpdateButton.Content = updates.Busy ? "Updating..." : _updating ? "Restarting..."
            : count > 0 ? $"Update all ({count})" : "Restart now";
        InstalledUpdateButton.IsEnabled = !_updating && !updates.Busy && _runtime.CanChangeProvider && !_changingPlugin;
        UpdateNotice.Text = _updateError ?? updates.Status ?? "";
        UpdateNotice.Visibility = (_updateError ?? updates.Status) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstalledUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !InstalledUpdateButton.IsEnabled) return;
        _updateError = null;
        var updates = _runtime.Packages.Updates;
        if (updates.Available.Count > 0)
            await updates.UpdateAsync();
        else if (RestartRequested is not null)
        {
            _updating = true; UpdateUpdateAction();
            try
            {
                _updateError = await RestartRequested();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _updateError = "Restart could not finish. Close and reopen TypeWhisper."; }
            finally { _updating = false; }
        }
        await RefreshRuntimeAsync();
    }
}
