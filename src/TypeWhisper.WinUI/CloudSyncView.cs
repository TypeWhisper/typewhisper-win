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
    private readonly Grid _audioRow = new() { ColumnSpacing = 16 };
    private readonly TextBlock _folder = Copy("");
    private readonly TextBlock _status = Copy("");
    private readonly TextBlock _access = Copy("");
    private readonly StackPanel _options = new() { Spacing = 12 };
    private readonly Button _choose = new HandCursorButton { Content = "Choose folder…", CornerRadius = new(8) };
    private readonly Button _sync = new HandCursorButton { Content = "Sync now", CornerRadius = new(8) };
    private bool _refreshing;
    private bool _picking;

    internal CloudSyncView()
    {
        WinUICloudSync.Initialize(DispatcherQueue);
        var body = new StackPanel { Spacing = 12 }; Content = body;
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(SettingsHelp.Label("Sync Dictionary & Snippets", "Choose the same folder on your Mac and Windows PC. iCloud for Windows, OneDrive or Dropbox must keep it available locally. Changes and deletions synchronize in both directions. Personal words, corrections and snippets are included, and History when you turn it on below. Audio, credentials and settings stay on this device.", 18));
        AppToggleSwitch.Configure(_enabled); AutomationProperties.SetName(_enabled, "Enable cloud folder sync");
        Grid.SetColumn(_enabled, 1); row.Children.Add(_enabled); body.Children.Add(row);
        body.Children.Add(_access);
        body.Children.Add(_options);
        _options.Children.Add(_folder);
        var historyRow = new Grid { ColumnSpacing = 16 };
        historyRow.ColumnDefinitions.Add(new()); historyRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        historyRow.Children.Add(SettingsHelp.Label("Sync History & Inbox", "Copies the text of all History entries and their Inbox state to the sync folder, and shows entries from your Mac and iPhone in History. Deleting an entry in History removes it everywhere; automatic cleanup stays on this PC. Turn on audio below to include recordings of new entries.", 14));
        AppToggleSwitch.Configure(_history); AutomationProperties.SetName(_history, "Sync History and Inbox");
        Grid.SetColumn(_history, 1); historyRow.Children.Add(_history);
        _options.Children.Add(historyRow);
        _history.Toggled += (_, _) => { if (!_refreshing) WinUICloudSync.SetHistoryEnabled(_history.IsOn); Refresh(); };
        _audioRow.ColumnDefinitions.Add(new()); _audioRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _audioRow.Children.Add(SettingsHelp.Label("Sync Audio for New Entries", "Copies the audio of entries created from now on, so you can listen to them on your other devices. Existing audio stays on the device that recorded it. Keep dictation audio must be on in History & Sync for this PC to save audio.", 14));
        AppToggleSwitch.Configure(_audio); AutomationProperties.SetName(_audio, "Sync audio for new entries");
        Grid.SetColumn(_audio, 1); _audioRow.Children.Add(_audio);
        _options.Children.Add(_audioRow);
        _audio.Toggled += (_, _) => { if (!_refreshing) WinUICloudSync.SetHistoryAudioEnabled(_audio.IsOn); Refresh(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_choose); actions.Children.Add(_sync); _options.Children.Add(actions);
        body.Children.Add(_status);
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _options.Children.Add(new HyperlinkButton { Content = "Set up iCloud Drive for Windows", NavigateUri = new("https://support.apple.com/en-us/118443") });
        _enabled.Toggled += async (_, _) =>
        {
            if (_refreshing) return;
            if (_enabled.IsOn && WinUICloudSync.Preferences.Folder is null) await ChooseAsync(enable: true);
            else WinUICloudSync.Configure(WinUICloudSync.Preferences.Folder, _enabled.IsOn);
            Refresh();
        };
        _choose.Click += async (_, _) => await ChooseAsync(enable: true);
        _sync.Click += async (_, _) => await WinUICloudSync.SyncAsync();
        Loaded += (_, _) => { WinUICloudSync.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => WinUICloudSync.Changed -= Refresh;
        Refresh();
    }
    private async Task ChooseAsync(bool enable)
    {
        if (_picking) return;
        _picking = true; WinUICloudSync.ChoosingFolder = true; Refresh();
        try
        {
            var picker = new FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId) { Title = "Choose the shared TypeWhisper sync folder" };
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await WinUICloudSync.WaitForIdleAsync();
                WinUICloudSync.Configure(CloudFolderSyncEngine.SyncFolder(folder.Path), enable);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _status.Text = "Could not select folder: " + ex.Message; }
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
        _audioRow.Visibility = WinUICloudSync.HistoryEnabled ? Visibility.Visible : Visibility.Collapsed;
        _enabled.IsEnabled = !_picking && !WinUICloudSync.Busy && (WinUICloudSync.CanUse || preferences.Enabled);
        _access.Text = WinUICloudSync.CanUse ? "" : "Requires a commercial license.";
        _access.Visibility = WinUICloudSync.CanUse ? Visibility.Collapsed : Visibility.Visible;
        _options.Visibility = preferences.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _folder.Text = preferences.Folder is { } folder ? $"{CloudFolderSyncProviderDetector.Detect(folder)} · {folder}" : "No folder selected";
        _status.Text = WinUICloudSync.Status;
        _choose.IsEnabled = _sync.IsEnabled = !_picking && !WinUICloudSync.Busy && WinUICloudSync.CanUse;
        _refreshing = false;
    }
    private static TextBlock Copy(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
}
