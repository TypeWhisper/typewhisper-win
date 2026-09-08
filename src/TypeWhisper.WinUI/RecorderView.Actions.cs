using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TypeWhisper.Presentation;
using global::Windows.System;
using global::Windows.Media.Playback;

namespace TypeWhisper.WinUI;

public sealed partial class RecorderView
{
    private void RefreshLibraryActions()
    {
        var selected = SelectedRecording;
        var visible = _libraryOpen && selected is not null ? Visibility.Visible : Visibility.Collapsed;
        LibraryPlayButton.Visibility = LibraryQueueButton.Visibility = LibraryFolderButton.Visibility = LibraryDeleteButton.Visibility = visible;
        var busy = _recorder?.Busy == true || !_libraryMutation.IsCompleted || _libraryConfirmation is not null;
        var active = _recorder?.State is RecorderState.Recording or RecorderState.Paused or RecorderState.SaveFailed;
        LibraryPlayButton.IsEnabled = selected is { Error: null } && !busy && !active;
        LibraryQueueButton.IsEnabled = selected is { Error: null } && !busy && !active;
        LibraryFolderButton.IsEnabled = selected is not null && !busy;
        LibraryDeleteButton.IsEnabled = selected is not null && !busy;
        LibraryPlayButton.Content = (_libraryPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? "Pause" : "Play audio") + " \u00b7 Enter";
    }
    private void LibrarySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingLibrary) return;
        if (SelectedRecording?.FilePath != _libraryPlayingPath) StopAudioPlayback();
        RefreshLibraryActions();
    }
    private void LibraryPlay_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryPlayButton.IsEnabled && SelectedRecording is { Error: null } entry) PlayLibraryAudio(entry.FilePath, entry.Name);
    }
    private void LibraryQueue_Click(object sender, RoutedEventArgs e)
    { if (LibraryQueueButton.IsEnabled && SelectedRecording is { } entry) RequestTranscribe(entry.FilePath); }
    private void LibraryFolder_Click(object sender, RoutedEventArgs e)
    { if (LibraryFolderButton.IsEnabled && SelectedRecording is { } entry) OpenLibraryFile(entry.FilePath); }
    private async void LibraryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!LibraryDeleteButton.IsEnabled || !_libraryMutation.IsCompleted || SelectedRecording is not { } entry) return;
        _libraryMutation = DeleteLibraryEntryAsync(entry);
        RefreshLibraryActions();
        try { await _libraryMutation; }
        finally { if (!_libraryClosing) RefreshLibraryActions(); }
    }
    private void Recorder_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_libraryClosing || _libraryConfirmation is not null) return;
        foreach (var key in new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift })
            if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != this; node = VisualTreeHelper.GetParent(node))
            if (node is TextBox or PasswordBox or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Slider or MediaPlayerElement) return;
        if (e.Key == VirtualKey.Enter)
        {
            if (PrimaryButton.Visibility == Visibility.Visible && PrimaryButton.IsEnabled) Primary_Click(this, new RoutedEventArgs());
            else LibraryPlay_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (_libraryOpen && e.Key == VirtualKey.Delete) { LibraryDelete_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (_libraryOpen && e.Key == VirtualKey.F) { LibraryFolder_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (_libraryOpen && e.Key == VirtualKey.T) { LibraryQueue_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
}
