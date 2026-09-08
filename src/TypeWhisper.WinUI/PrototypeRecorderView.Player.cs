using Microsoft.UI.Xaml;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeRecorderView
{
    internal Func<bool>? CanPlayAudio { get; set; }
    internal Func<Task>? PrepareAudioPlayback { get; set; }
    private MediaPlayer? _libraryPlayer;
    private MediaSource? _librarySource;
    private int _libraryPlaybackGeneration;

    internal void StopAudioPlayback()
    {
        ++_libraryPlaybackGeneration;
        var player = _libraryPlayer;
        var source = _librarySource;
        _libraryPlayer = null; _librarySource = null;
        try { LibraryPlayerElement.SetMediaPlayer(null); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        try { player?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        try { source?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        LibraryPlaybackPanel.Visibility = Visibility.Collapsed;
    }

    private void StopLibraryPlayback_Click(object sender, RoutedEventArgs e)
    {
        StopAudioPlayback();
        LibraryRefreshButton.Focus(FocusState.Programmatic);
    }

    private async void PlayLibraryAudio(string path, string name)
    {
        if (_libraryClosing || !_presented || !_libraryOpen) return;
        if (CanPlayAudio?.Invoke() != true)
        {
            LibraryStatus.Text = "Finish the current recording or processing before playing audio.";
            return;
        }
        StopAudioPlayback();
        var generation = _libraryPlaybackGeneration;
        LibraryPlaybackPanel.Visibility = Visibility.Visible;
        LibraryPlaybackTitle.Text = "Loading " + name;
        try
        {
            if (PrepareAudioPlayback is not null) await PrepareAudioPlayback();
            if (!Current()) { if (generation == _libraryPlaybackGeneration) StopAudioPlayback(); return; }
            var verified = _library.ResolvePlaybackPath(path);
            var file = await StorageFile.GetFileFromPathAsync(verified);
            if (!Current()) { if (generation == _libraryPlaybackGeneration) StopAudioPlayback(); return; }
            _library.ResolvePlaybackPath(verified);
            _librarySource = MediaSource.CreateFromStorageFile(file);
            var player = _libraryPlayer = new MediaPlayer { AutoPlay = false };
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(player, _libraryPlayer)) return;
                StopAudioPlayback();
                LibraryStatus.Text = "Audio playback failed. Check the recording and your audio output, then try again.";
            });
            player.Source = _librarySource;
            LibraryPlayerElement.SetMediaPlayer(player);
            LibraryPlaybackTitle.Text = name;
            player.Play();
            LibraryStatus.Text = "Playing saved audio inside TypeWhisper.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != _libraryPlaybackGeneration) return;
            StopAudioPlayback();
            LibraryStatus.Text = "Could not play recording: " + ex.Message;
        }
        bool Current() => generation == _libraryPlaybackGeneration && !_libraryClosing && _presented && _libraryOpen && CanPlayAudio?.Invoke() == true;
    }
}
