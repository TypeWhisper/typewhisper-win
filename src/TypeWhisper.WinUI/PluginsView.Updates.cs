using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed partial class PluginsView
{
    internal Func<Task<string?>>? RestartRequested { get; set; }
    private bool _updating;
    private string? _updateError;
    private string InstalledCategories(string? id, IReadOnlyList<string>? declared)
    {
        var categories = declared?.ToList() ?? [];
        if (categories.Count == 0 && id is not null && _runtime is not null)
        {
            if (_runtime.DictationProviders.Any(provider => provider.PluginId == id)) categories.Add("transcription");
            if (_runtime.PluginRuntime.LlmProviders.Any(provider => provider.PluginId == id)) categories.Add("llm");
        }
        return string.Join(" / ", categories.Select(value => value == "llm" ? "LLM" : string.IsNullOrWhiteSpace(value) ? "" : char.ToUpperInvariant(value[0]) + value[1..]));
    }
    private string? RuntimeUpdateStatus(string? id) => id is null || _runtime is null ? null
        : _runtime.Packages.Store.PendingRestart(id) ? "Restart required"
        : _runtime.Packages.Updates.HasUpdate(id) ? "Update available" : null;

    private void UpdateUpdateAction()
    {
        if (_runtime is null) return;
        var updates = _runtime.Packages.Updates;
        var id = _page == Page.Detail ? Path.GetFileName(_opened?.Id) : null;
        var count = _page == Page.List ? updates.Available.Count : id is not null && updates.HasUpdate(id) ? 1 : 0;
        var restart = _page == Page.List ? updates.RestartRequired : id is not null && _runtime.Packages.Store.PendingRestart(id);
        InstalledUpdateButton.Visibility = _page != Page.Settings && (count > 0 || restart || updates.Busy || _updating)
            ? Visibility.Visible : Visibility.Collapsed;
        InstalledUpdateButton.Content = updates.Busy ? "Updating..." : _updating ? "Restarting..."
            : count > 0 ? _page == Page.List ? $"Update all ({count})" : "Update" : "Restart now";
        InstalledUpdateButton.IsEnabled = !_updating && !updates.Busy && _runtime.CanChangeProvider && !_changingPlugin;
        UpdateNotice.Text = _updateError ?? updates.Status ?? "";
        UpdateNotice.Visibility = _page != Page.Settings && (_updateError ?? updates.Status) is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstalledUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || !InstalledUpdateButton.IsEnabled) return;
        _updateError = null;
        var id = _page == Page.Detail ? Path.GetFileName(_opened?.Id) : null;
        var updates = _runtime.Packages.Updates;
        if (id is null ? updates.Available.Count > 0 : updates.HasUpdate(id))
            await updates.UpdateAsync(id);
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
