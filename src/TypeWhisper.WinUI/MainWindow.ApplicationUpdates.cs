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
    private AppUpdateReminder? _updateReminder;
    private AppUpdateReminder UpdateReminder
    {
        get
        {
            if (_updateReminder is not null) return _updateReminder;
            _updateReminder = new(WinUIProfile.DataPath("update-reminder.json"));
            if (_updateReminder.Error is { } error) AppDiagnostics.Write("app.update.reminder.load-failed", error);
            return _updateReminder;
        }
    }
    /// <summary>Shows a Windows notification from the tray icon; the action runs when it is clicked.</summary>
    internal Action<string, string, Action>? ShowTrayNotification { get; set; }
    private PluginAutoUpdatePreference? _pluginAutoUpdates;
    private PluginAutoUpdatePreference PluginAutoUpdates => _pluginAutoUpdates ??= new(WinUIProfile.DataPath("plugin-auto-update.txt"));

    // Checks the catalog shortly after startup and once a day. Updates are downloaded while
    // nothing is recorded or processed and take effect at the next start.
    private async Task UpdatePluginsAutomaticallyAsync()
    {
        var updates = _dictation.Packages.Updates;
        var wait = TimeSpan.FromMinutes(1);
        // An app update check or download keeps plugins unchanged, so its restart is not refused.
        bool CanUpdate() => PluginAutoUpdates.Enabled && _dictation.CanChangeProvider && !_dictation.IsRecording && !_dictation.Models.Busy && !updates.Busy
            && !ApplicationUpdates.Busy;
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

    // Checks the app's update channel shortly after startup and once a day, as Sparkle does on macOS.
    // A newer version is announced with a Windows notification; clicking it offers to install, wait or skip.
    private async Task CheckApplicationUpdatesAutomaticallyAsync()
    {
        var updates = ApplicationUpdates;
        var plugins = _dictation.Packages.Updates;
        // The plugin check starts after one minute; start later so both rarely meet.
        var wait = TimeSpan.FromMinutes(2);
        while (true)
        {
            await Task.Delay(wait);
            if (_closing || _profileRestoreClosing) return;
            // Busy, recording, a plugin update or an installation without updates: look again soon.
            wait = TimeSpan.FromMinutes(15);
            if (!updates.CanCheck || _dictation.IsRecording || plugins.Checking || plugins.Busy) continue;
            wait = AppUpdateReminder.Interval;
            await updates.CheckAsync();
            if (_closing || _profileRestoreClosing) return;
            // An older version is offered only after a channel switch; that stays a decision in Settings.
            if (updates.Offer is { IsDowngrade: false } offer) AnnounceApplicationUpdate(offer);
        }
    }

    private void AnnounceApplicationUpdate(AppUpdateOffer offer)
    {
        if (!UpdateReminder.ShouldNotify(offer.Version, DateTimeOffset.Now)) return;
        ShowTrayNotification?.Invoke(Loc.T("TypeWhisper {0} is available", offer.Version),
            Loc.T("Click to install it now, later or skip this version."), () => DispatcherQueue.TryEnqueue(() => ShowApplicationUpdateNotice(offer)));
    }

#if DEBUG
    // Visual fixture: announces a made-up version. Debug builds cannot install, so the main button only opens Settings.
    internal void ShowApplicationUpdateFixture() => AnnounceApplicationUpdate(new("9.9.9", false));
#endif

    private void ShowApplicationUpdateNotice(AppUpdateOffer offer)
    {
        if (_closing || _profileRestoreClosing) return;
        ShowNotice(new AppNotice(Loc.T("Version {0} is available. You have version {1}.", offer.Version, WindowsApplicationUpdates.CurrentVersion),
            Loc.T("Update available"), IsError: false, Duration: TimeSpan.FromMinutes(1), Actions: [
                new(Loc.T("Download and restart"), () => InstallApplicationUpdate(offer), Primary: true),
                new(Loc.T("Later"), () => AnswerUpdateNotice(reminder => reminder.Later(DateTimeOffset.Now))),
                new(Loc.T("Skip this version"), () => AnswerUpdateNotice(reminder => reminder.Skip(offer.Version)))]));
    }

    // An answer that cannot be saved still holds until TypeWhisper restarts.
    private void AnswerUpdateNotice(Action<AppUpdateReminder> answer)
    {
        answer(UpdateReminder);
        if (UpdateReminder.Error is { } error) AppDiagnostics.Write("app.update.reminder.save-failed", error);
    }

    // Settings shows the download progress and explains why a restart has to wait.
    private async void InstallApplicationUpdate(AppUpdateOffer offer)
    {
        OpenSettingsPage("Account & about");
        // A later check may have replaced the offer; then Settings shows the current state.
        if (ApplicationUpdates.Offer?.Version == offer.Version) await ApplicationUpdates.InstallAsync();
    }

    internal async void ShowApplicationUpdates()
    {
        OpenSettings();
        _settingsWindow?.ShowCategory("Account & about");
        await Task.WhenAll(ApplicationUpdates.CheckAsync(), _dictation.Packages.Updates.RefreshAsync());
    }
}
