using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    internal Func<Action, Task<string?>>? InstallApplicationUpdateAsync { get; set; }
    private AppUpdateController? _applicationUpdates;
    private AppUpdateController ApplicationUpdates => _applicationUpdates ??= new(
        new AppUpdatePreferences(WinUIProfile.DataPath("updates.json"), WindowsApplicationUpdates.CurrentVersion),
        new WindowsApplicationUpdates(), action =>
        {
            if (_closing || _profileRestoreClosing || !_dictation.CanChangeProvider || _dictation.Packages.Updates.Busy || _dictation.Models.Busy)
                return Task.FromResult<string?>(Loc.T("Finish recording, processing and plugin updates before restarting TypeWhisper."));
            return InstallApplicationUpdateAsync?.Invoke(action) ?? Task.FromResult<string?>(Loc.T("Restart is currently unavailable."));
        });
    private PluginAutoUpdatePreference? _pluginAutoUpdates;
    private PluginAutoUpdatePreference PluginAutoUpdates => _pluginAutoUpdates ??= new(WinUIProfile.DataPath("plugin-auto-update.txt"));

    // Checks the catalog shortly after startup and once a day. Updates are downloaded while
    // nothing is recorded or processed and take effect at the next start.
    private async Task UpdatePluginsAutomaticallyAsync()
    {
        var updates = _dictation.Packages.Updates;
        var wait = TimeSpan.FromMinutes(1);
        bool CanUpdate() => PluginAutoUpdates.Enabled && _dictation.CanChangeProvider && !_dictation.IsRecording && !_dictation.Models.Busy && !updates.Busy;
        while (true)
        {
            await Task.Delay(wait);
            if (_closing || _profileRestoreClosing) return;
            // Turned off or busy: look again soon. A failed check or download waits for the next day.
            wait = TimeSpan.FromMinutes(15);
            if (!CanUpdate()) continue;
            var refreshed = await updates.RefreshAsync();
            if (_closing || _profileRestoreClosing) return;
            // A recording may have started during the request.
            if (!CanUpdate()) continue;
            wait = TimeSpan.FromHours(24);
            // Never install from an earlier catalog after a failed or skipped check.
            if (refreshed && updates.Available.Count > 0) await updates.UpdateAsync();
        }
    }

    internal async void ShowApplicationUpdates()
    {
        OpenSettings();
        _settingsWindow?.ShowCategory("Account & about");
        await Task.WhenAll(ApplicationUpdates.CheckAsync(), _dictation.Packages.Updates.RefreshAsync());
    }
}
