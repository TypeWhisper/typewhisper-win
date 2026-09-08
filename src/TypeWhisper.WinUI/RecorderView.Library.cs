using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class RecorderView
{
    private readonly RecorderLibraryStore _library = new(WinUIProfile.DataPath("recordings"));
    private CancellationTokenSource? _libraryCancellation;
    private int _libraryGeneration;
    private bool _libraryOpen;
    private bool _libraryClosing;
    private Task _libraryPending = Task.CompletedTask;
    private Task _libraryMutation = Task.CompletedTask;
    private ContentDialog? _libraryConfirmation;
    private string? _librarySavedPath;
    private string? _selectRecordingPath;
    private bool _refreshingLibrary;
    private RecorderLibraryEntry? SelectedRecording => (LibraryEntries.SelectedItem as ListViewItem)?.Tag as RecorderLibraryEntry;
    internal Func<string, bool>? IsQueuedSource { get; set; }

    private void ShowLibrary(bool open)
    {
        StopAudioPlayback();
        _libraryOpen = open;
        RecordingContent.Visibility = _libraryOpen ? Visibility.Collapsed : Visibility.Visible;
        LibraryPanel.Visibility = _libraryOpen ? Visibility.Visible : Visibility.Collapsed;
        RecorderTabs.SetSelected(_libraryOpen ? "recordings" : "record");
        if (_libraryOpen) BeginLibraryRefresh();
        Refresh();
        if (_presented) FocusEntry();
    }

    private void LibraryRefresh_Click(object sender, RoutedEventArgs e) => BeginLibraryRefresh();

    private void BeginLibraryRefresh()
    {
        if (_libraryClosing) return;
        _libraryCancellation?.Cancel();
        _libraryCancellation?.Dispose();
        _libraryCancellation = new();
        _libraryPending = RefreshLibraryAsync(++_libraryGeneration, _libraryCancellation.Token);
    }

    private async Task RefreshLibraryAsync(int generation, CancellationToken cancellationToken)
    {
        LibraryStatus.Text = "Loading recordings…";
        try
        {
            var entries = await _library.ReadAsync(cancellationToken);
            if (_libraryClosing || generation != _libraryGeneration) return;
            var selected = _selectRecordingPath ?? SelectedRecording?.FilePath;
            _selectRecordingPath = null;
            _refreshingLibrary = true;
            try
            {
                LibraryEntries.Items.Clear();
                foreach (var entry in entries) LibraryEntries.Items.Add(BuildLibraryEntry(entry));
                LibraryEntries.SelectedItem = LibraryEntries.Items.OfType<ListViewItem>().FirstOrDefault(item => (item.Tag as RecorderLibraryEntry)?.FilePath == selected)
                    ?? LibraryEntries.Items.FirstOrDefault();
            }
            finally { _refreshingLibrary = false; }
            if (SelectedRecording?.FilePath != _libraryPlayingPath) StopAudioPlayback();
            RefreshLibraryActions();
            if (LibraryEntries.SelectedItem is { } item) LibraryEntries.ScrollIntoView(item);
            if (_libraryOpen && _presented) LibraryEntries.Focus(FocusState.Programmatic);
            LibraryStatus.Text = entries.Count == 0 ? "No saved recordings yet." : $"{entries.Count} saved recording{(entries.Count == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_libraryClosing && generation == _libraryGeneration) LibraryStatus.Text = "Could not load recordings: " + ex.Message; }
    }

    private FrameworkElement BuildLibraryEntry(RecorderLibraryEntry entry)
    {
        var row = new StackPanel { Spacing = 8 };
        row.Children.Add(new TextBlock { Text = System.IO.Path.GetFileNameWithoutExtension(entry.Name), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var duration = entry.Duration?.ToString(@"hh\:mm\:ss") ?? "Duration unavailable";
        var date = entry.CreatedAt == DateTimeOffset.MinValue ? "Date unavailable" : entry.CreatedAt.ToLocalTime().ToString("g");
        row.Children.Add(new TextBlock { Text = $"{duration} · {date}", FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["MutedBrush"] });
        if (entry.Error is not null) row.Children.Add(new TextBlock { Text = "Could not read recording: " + entry.Error, TextWrapping = TextWrapping.Wrap });
        row.Padding = new Thickness(12);
        return new ListViewItem { Content = row, Tag = entry };
    }

    private void OpenLibraryFile(string path)
    {
        if (_libraryClosing) return;
        try
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("The recording no longer exists.");
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { LibraryStatus.Text = "Could not open recording: " + ex.Message; }
    }

    private async Task DeleteLibraryEntryAsync(RecorderLibraryEntry entry)
    {
        if (_libraryClosing) return;
        try
        {
            if (IsQueuedSource?.Invoke(entry.FilePath) == true)
            { LibraryStatus.Text = "Remove this recording from the file queue before deleting it."; return; }
            var confirmation = _libraryConfirmation = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Delete recording?",
                Content = $"Permanently delete {entry.Name}? This cannot be undone.",
                PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary || _libraryClosing) return;
            if (IsQueuedSource?.Invoke(entry.FilePath) == true)
            { LibraryStatus.Text = "Remove this recording from the file queue before deleting it."; return; }
            StopAudioPlayback();
            _library.Delete(entry.FilePath, IsQueuedSource);
            if (_recorder?.ForgetDeletedFile(entry.FilePath) == true)
            {
                _recordingDeleted = true;
                Refresh();
            }
            BeginLibraryRefresh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { LibraryStatus.Text = "Could not delete recording: " + ex.Message; }
        finally { _libraryConfirmation = null; }
    }

    private void RequestTranscribe(string path)
    {
        if (_libraryClosing || !_libraryMutation.IsCompleted) return;
        if (!System.IO.File.Exists(path))
        {
            LibraryStatus.Text = "The recording no longer exists. Refresh the library.";
            if (_recorder?.ForgetDeletedFile(path) == true) { _recordingDeleted = true; Refresh(); }
            return;
        }
        TranscribeRequested?.Invoke(path);
    }

    private async Task ShutdownLibraryAsync()
    {
        _libraryClosing = true;
        StopAudioPlayback();
        _libraryGeneration++;
        _libraryCancellation?.Cancel();
        _libraryConfirmation?.Hide();
        await _libraryPending;
        await _libraryMutation;
        _libraryCancellation?.Dispose();
        _libraryCancellation = null;
    }
}
