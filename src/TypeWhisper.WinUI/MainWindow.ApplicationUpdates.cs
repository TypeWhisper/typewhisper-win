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
                return Task.FromResult<string?>("Finish recording, processing and plugin updates before restarting TypeWhisper.");
            return InstallApplicationUpdateAsync?.Invoke(action) ?? Task.FromResult<string?>("Restart is currently unavailable.");
        });
    internal async void ShowApplicationUpdates()
    {
        OpenSettings();
        _settingsWindow?.ShowCategory("General");
        await ApplicationUpdates.CheckAsync();
    }
}
