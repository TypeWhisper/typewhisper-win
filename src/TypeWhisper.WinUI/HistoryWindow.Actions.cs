using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using global::Windows.ApplicationModel.DataTransfer;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryWindow
{
    private MediaPlayer? _player;
    private string? _playerId;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _playerTimer;
    private HandCursorButton? _playButton;
    private Slider? _timeline;
    private TextBlock? _position;
    private bool _updatingTimeline;

    private TranscriptionRecord[] SelectedRecords() => _selection
        .Select(id => _records.FirstOrDefault(record => record.Id == id)).OfType<TranscriptionRecord>().ToArray();

    private static HandCursorButton ActionButton(string label, Action click)
    {
        var button = new HandCursorButton { Content = label, Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => click();
        return button;
    }

    private MenuFlyout RowMenu(TranscriptionRecord record)
    {
        var menu = new MenuFlyout();
        void Add(string text, Action click, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var records = SelectedRecords();
            Add(Loc.T("Copy"), CopySelection);
            var export = new MenuFlyoutSubItem { Text = Loc.T("Export as…") };
            foreach (var (label, extension) in new[] { ("Markdown", ".md"), (Loc.T("Plain Text"), ".txt"), ("JSON", ".json") })
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) => _ = ExportAsync(extension);
                export.Items.Add(item);
            }
            menu.Items.Add(export);
            if (records.Any(item => item.InboxState == HistoryWorkspace.InboxOpen)) Add(Loc.T("Mark Complete"), () => _ = SetInboxAsync(true));
            if (records.Any(item => item.InboxState == HistoryWorkspace.InboxCompleted)) Add(Loc.T("Reopen"), () => _ = SetInboxAsync(false));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(records.Length > 1 ? Loc.T("Delete {0} Entries…", records.Length) : Loc.T("Delete…"), () => _ = DeleteSelectionAsync());
        };
        return menu;
    }

    private void CopySelection()
    {
        var records = SelectedRecords();
        if (records.Length == 0) return;
        // A single entry copies what is on screen, including unsaved edits.
        var text = records.Length == 1 && _editor is not null && _editedId == records[0].Id
            ? _editor.Text : string.Join("\n\n", records.Select(record => record.DisplayText));
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _notice.Text = records.Length == 1 ? Loc.T("Copied to the clipboard.") : Loc.T("Copied {0} entries to the clipboard.", records.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History copy failed: {0}", ex);
            _notice.Text = Loc.T("The clipboard is busy. Try copying again.");
        }
    }

    private async Task SetInboxAsync(bool completed)
    {
        var ids = SelectedRecords().Select(record => record.Id).ToArray();
        if (ids.Length == 0) return;
        try
        {
            var changed = await _actions.SetInboxCompletedAsync(ids, completed);
            _notice.Text = changed == 0 ? Loc.T("Nothing to change.") : completed
                ? changed == 1 ? Loc.T("Marked 1 entry complete.") : Loc.T("Marked {0} entries complete.", changed)
                : changed == 1 ? Loc.T("Reopened 1 entry.") : Loc.T("Reopened {0} entries.", changed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History Inbox change failed: {0}", ex);
            _notice.Text = Loc.T("The Inbox change could not be saved. Try again.");
        }
        await RefreshAsync();
    }

    private async Task ExportAsync(string extension)
    {
        var ids = SelectedRecords().Select(record => record.Id).ToArray();
        if (ids.Length == 0) return;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = ids.Length == 1 ? "transcript" : "history-selection",
                Title = ids.Length == 1 ? Loc.T("Export transcript") : Loc.T("Export {0} entries", ids.Length)
            };
            picker.FileTypeChoices.Add(extension switch { ".txt" => Loc.T("Plain Text"), ".json" => "JSON", _ => "Markdown" }, new List<string> { extension });
            var file = await picker.PickSaveFileAsync();
            if (_closing || file is null) return;
            await _actions.ExportFileAsync(ids, file.Path);
            _notice.Text = ids.Length == 1 ? Loc.T("Transcript exported.") : Loc.T("Exported {0} entries.", ids.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History export failed: {0}", ex);
            _notice.Text = Loc.T("Export failed. Choose a writable location and try again.");
        }
    }

    private async Task DeleteSelectionAsync()
    {
        var ids = SelectedRecords().Select(record => record.Id).ToArray();
        if (ids.Length == 0 || _dialogOpen) return;
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = ids.Length == 1 ? Loc.T("Delete this entry?") : Loc.T("Delete {0} entries?", ids.Length),
            Content = Loc.T("The transcript and any saved audio are removed from this PC. This cannot be undone."),
            PrimaryButtonText = Loc.T("Delete"), CloseButtonText = Loc.T("Cancel"), DefaultButton = ContentDialogButton.Close
        };
        _dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        StopAudioPlayback();
        try
        {
            if (await _actions.DeleteAsync(ids))
            {
                _editor = null;
                _editedId = null;
                _selection.Clear();
                _notice.Text = ids.Length == 1 ? Loc.T("Entry deleted.") : Loc.T("Deleted {0} entries.", ids.Length);
            }
            else _notice.Text = Loc.T("The entries could not be deleted. Your history was not changed.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History delete failed: {0}", ex);
            _notice.Text = Loc.T("The entries could not be deleted. Your history was not changed.");
        }
        await RefreshAsync();
    }

    // Audio ----------------------------------------------------------------------------------

    private UIElement AudioStrip(TranscriptionRecord record)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var playing = _playerId == record.Id && _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        _playButton = new HandCursorButton { Content = Glyph(playing ? "pause" : "play", 16), Style = (Style)Application.Current.Resources["SecondaryButtonStyle"], MinWidth = 40 };
        AutomationProperties.SetName(_playButton, playing ? Loc.T("Pause audio") : Loc.T("Play audio"));
        _playButton.Click += (_, _) => _ = ToggleAudioAsync(record.Id);
        row.Children.Add(_playButton);
        _timeline = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_timeline, Loc.T("Audio position"));
        _timeline.ValueChanged += Timeline_ValueChanged;
        Grid.SetColumn(_timeline, 1); row.Children.Add(_timeline);
        _position = Text("0:00 / " + Duration(record.DurationSeconds), 12, muted: true);
        _position.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_position, 2); row.Children.Add(_position);
        var folder = ActionButton(Loc.T("Show in Folder"), () => _ = ShowAudioAsync(record.Id));
        Grid.SetColumn(folder, 3); row.Children.Add(folder);
        if (_playerId == record.Id) UpdatePosition();
        return new Border { Child = row, Padding = new Thickness(10, 8, 12, 8), CornerRadius = new CornerRadius(10),
            Background = Brush("SurfaceBrush"), BorderBrush = Brush("HairlineBrush"), BorderThickness = new Thickness(1) };
    }

    private string? AudioPath(string id)
    {
        if (_audio is not { } audio) return null;
        var record = audio.Records.FirstOrDefault(item => item.Id == id);
        return record is null ? null : audio.ResolveAudioPath(record.AudioFileName);
    }

    private async Task ToggleAudioAsync(string id)
    {
        if (_closing) return;
        if (_player is { } current && _playerId == id)
        {
            if (current.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) current.Pause();
            else current.Play();
            UpdatePosition();
            return;
        }
        if (CanPlayAudio?.Invoke() == false) { _notice.Text = Loc.T("Finish the current recording or operation before playing audio."); return; }
        StopAudioPlayback();
        try
        {
            if (PrepareAudioPlayback is { } prepare) await prepare();
            var path = await Task.Run(() => AudioPath(id));
            if (_closing || Opened?.Id != id) return;
            if (path is null) { _notice.Text = Loc.T("The saved audio is missing. The transcript is still available."); return; }
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (_closing || Opened?.Id != id) return;
            var player = _player = new MediaPlayer { AutoPlay = true, Source = MediaSource.CreateFromStorageFile(file) };
            _playerId = id;
            player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_player, player)) return;
                player.Pause();
                player.PlaybackSession.Position = TimeSpan.Zero;
                UpdatePosition();
            });
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_player, player)) return;
                StopAudioPlayback();
                _notice.Text = Loc.T("Audio playback failed. Check your audio output and try again.");
            });
            player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (ReferenceEquals(_player, player)) UpdatePosition(); });
            _playerTimer ??= DispatcherQueue.CreateTimer();
            _playerTimer.Interval = TimeSpan.FromMilliseconds(250);
            _playerTimer.Tick -= PlayerTimer_Tick;
            _playerTimer.Tick += PlayerTimer_Tick;
            _playerTimer.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History playback failed: {0}", ex);
            StopAudioPlayback();
            _notice.Text = Loc.T("The saved audio could not be played. Check the file and audio output.");
        }
    }

    private void PlayerTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => UpdatePosition();

    private void UpdatePosition()
    {
        if (_closing || _player is not { } player || _timeline is null || _position is null || _playButton is null) return;
        var session = player.PlaybackSession;
        var duration = session.NaturalDuration;
        _updatingTimeline = true;
        _timeline.Maximum = Math.Max(1, duration.TotalSeconds);
        _timeline.Value = Math.Clamp(session.Position.TotalSeconds, 0, _timeline.Maximum);
        _timeline.IsEnabled = duration > TimeSpan.Zero && session.CanSeek;
        _updatingTimeline = false;
        _position.Text = $"{(int)session.Position.TotalMinutes}:{session.Position.Seconds:00} / {(int)duration.TotalMinutes}:{duration.Seconds:00}";
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        _playButton.Content = Glyph(playing ? "pause" : "play", 16);
        AutomationProperties.SetName(_playButton, playing ? Loc.T("Pause audio") : Loc.T("Play audio"));
    }

    private void Timeline_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingTimeline || _player is null) return;
        try { _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _notice.Text = Loc.T("Could not seek in this recording."); }
    }

    internal void StopAudioPlayback()
    {
        _playerTimer?.Stop();
        var player = _player;
        _player = null;
        _playerId = null;
        try { player?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Debug.WriteLine(ex); }
        if (_closing || _playButton is null) return;
        _playButton.Content = Glyph("play", 16);
        if (_timeline is not null) { _updatingTimeline = true; _timeline.Value = 0; _timeline.IsEnabled = false; _updatingTimeline = false; }
    }

    private async Task ShowAudioAsync(string id)
    {
        try
        {
            var path = await Task.Run(() => AudioPath(id));
            if (path is null) { _notice.Text = Loc.T("The saved audio is no longer available. Your transcript was not changed."); return; }
            // The resolver only returns verified WAV files owned by the History audio store.
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("History audio folder failed: {0}", ex);
            _notice.Text = Loc.T("The audio folder could not be opened.");
        }
    }
}
