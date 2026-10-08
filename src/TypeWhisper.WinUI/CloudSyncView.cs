using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.WinUI;

internal sealed class CloudSyncView : UserControl
{
    private readonly ToggleSwitch _enabled = new();
    private readonly ToggleSwitch _history = new();
    private readonly ToggleSwitch _audio = new();
    private readonly SettingsRow _syncRow = new();
    private readonly SettingsRow _historyRow = new();
    private readonly SettingsRow _audioRow = new();
    private readonly SettingsRow _folderRow = new();
    private readonly Button _choose = new HandCursorButton { Content = Loc.T("Choose folder…"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
    private readonly Button _sync = new HandCursorButton { Content = Loc.T("Sync now"), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"] };
    private bool _refreshing;
    private bool _picking;
    private string? _pickError;

    internal CloudSyncView()
    {
        WinUICloudSync.Initialize(DispatcherQueue);
        // One card: the switch, then what it synchronizes and where.
        var body = new SettingsCard(); Content = body;
        AppToggleSwitch.Configure(_enabled); AutomationProperties.SetName(_enabled, Loc.T("Enable cloud folder sync"));
        _syncRow.Set(Loc.T("Sync Dictionary & Snippets"), "", Loc.T("Choose the same folder on your Mac and Windows PC. iCloud for Windows, OneDrive or Dropbox must keep it available locally. Changes and deletions synchronize in both directions. Personal words, corrections and snippets are included, and History when you turn it on below. Audio, credentials and settings stay on this device."), _enabled);
        body.Children.Add(_syncRow);
        AppToggleSwitch.Configure(_history); AutomationProperties.SetName(_history, Loc.T("Sync History and Inbox"));
        _historyRow.Set(Loc.T("Sync History & Inbox"), "", Loc.T("Copies the text of all History entries and their Inbox state to the sync folder, and shows entries from your Mac and iPhone in History. Deleting an entry in History removes it everywhere; automatic cleanup stays on this PC. Turn on audio below to include recordings of new entries."), _history);
        body.Children.Add(_historyRow);
        _history.Toggled += (_, _) => { if (!_refreshing) WinUICloudSync.SetHistoryEnabled(_history.IsOn); Refresh(); };
        AppToggleSwitch.Configure(_audio); AutomationProperties.SetName(_audio, Loc.T("Sync audio for new entries"));
        _audioRow.Set(Loc.T("Sync Audio for New Entries"), "", Loc.T("Copies the audio of entries created from now on, so you can listen to them on your other devices. Existing audio stays on the device that recorded it. Keep dictation audio must be on in History & Sync for this PC to save audio."), _audio);
        body.Children.Add(_audioRow);
        _audio.Toggled += (_, _) => { if (!_refreshing) WinUICloudSync.SetHistoryAudioEnabled(_audio.IsOn); Refresh(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_choose); actions.Children.Add(_sync);
        // The description shows the folder and its provider.
        _folderRow.Set(Loc.T("Folder"), control: actions);
        _folderRow.Below(new HyperlinkButton { Content = Loc.T("Set up iCloud Drive for Windows"), NavigateUri = new("https://support.apple.com/en-us/118443"), Padding = new Thickness(0) });
        body.Children.Add(_folderRow);
        _enabled.Toggled += async (_, _) =>
        {
            if (_refreshing) return;
            if (_enabled.IsOn && WinUICloudSync.Preferences.Folder is null) await ChooseAsync(enable: true);
            else WinUICloudSync.Configure(WinUICloudSync.Preferences.Folder, _enabled.IsOn);
            Refresh();
        };
        _choose.Click += async (_, _) => await ChooseAsync(enable: true);
        _sync.Click += async (_, _) => await WinUICloudSync.SyncAsync();
        ViewSubscriptions.Attach(this, () => { WinUICloudSync.Changed += Refresh; Refresh(); },
            () => WinUICloudSync.Changed -= Refresh);
        Refresh();
    }
    private async Task ChooseAsync(bool enable)
    {
        if (_picking) return;
        _picking = true; _pickError = null; WinUICloudSync.ChoosingFolder = true; Refresh();
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = Loc.T("Choose the shared TypeWhisper sync folder") };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await WinUICloudSync.WaitForIdleAsync();
                WinUICloudSync.Configure(CloudFolderSyncEngine.SyncFolder(folder.Path), enable);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _pickError = Loc.T("Could not select folder: {0}", ex.Message); }
        finally { _picking = false; WinUICloudSync.ChoosingFolder = false; Refresh(); }
    }
    private void Refresh()
    {
        _refreshing = true;
        var preferences = WinUICloudSync.Preferences;
        _enabled.IsOn = preferences.Enabled;
        _history.IsOn = WinUICloudSync.HistoryEnabled;
        _history.IsEnabled = !_picking && !WinUICloudSync.Busy && WinUICloudSync.CanUse;
        _audio.IsOn = WinUICloudSync.HistoryAudioEnabled;
        _audio.IsEnabled = _history.IsEnabled;
        _historyRow.Visibility = _folderRow.Visibility = preferences.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _audioRow.Visibility = preferences.Enabled && WinUICloudSync.HistoryEnabled ? Visibility.Visible : Visibility.Collapsed;
        _enabled.IsEnabled = !_picking && !WinUICloudSync.Busy && (WinUICloudSync.CanUse || preferences.Enabled);
        _syncRow.Description = WinUICloudSync.CanUse ? "" : Loc.T("Requires a commercial license.");
        _syncRow.Status = _pickError ?? WinUICloudSync.Status;
        _folderRow.Description = preferences.Folder is { } folder ? $"{CloudFolderSyncProviderDetector.Detect(folder)} · {folder}" : Loc.T("No folder selected");
        _choose.IsEnabled = _sync.IsEnabled = !_picking && !WinUICloudSync.Busy && WinUICloudSync.CanUse;
        _refreshing = false;
    }
}
