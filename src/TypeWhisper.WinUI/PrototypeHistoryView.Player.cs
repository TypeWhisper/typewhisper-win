using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Storage;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeHistoryView
{
    internal Func<bool>? CanPlayAudio { get; set; }
    internal Func<Task>? PrepareAudioPlayback { get; set; }
    private MediaPlayer? _audioPlayer;
    private MediaSource? _audioSource;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _audioTimer;
    private int _playbackGeneration;
    private bool _audioLoading;
    private bool _updatingAudioPosition;
    private bool AudioPlayerActive => _audioLoading || _audioPlayer is not null;

    internal void StopAudioPlayback()
    {
        ++_playbackGeneration;
        _audioLoading = false;
        _audioTimer?.Stop();
        var player = _audioPlayer;
        var source = _audioSource;
        _audioPlayer = null;
        _audioSource = null;
        try { player?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        try { source?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine(ex); }
        if (!_closing)
        {
            AudioTimeline.IsEnabled = false;
            _updatingAudioPosition = true;
            AudioTimeline.Value = 0;
            _updatingAudioPosition = false;
            AudioPosition.Text = "0:00 / 0:00";
            SetAudioPlayButton(false);
        }
    }

    private void SetAudioPlayButton(bool playing)
    {
        PlayAudioIcon.Kind = playing ? "pause" : "play";
        PlayAudioLabel.Text = playing ? "Pause" : "Play audio";
        AutomationProperties.SetName(PlayAudioButton, playing ? "Pause history audio" : "Play history audio");
        PlayAudioButton.IsEnabled = !_audioLoading && !_acting;
    }

    private async void PlayAudio_Click(object sender, RoutedEventArgs e)
    {
        try { await TrackWriteAsync(ToggleAudioPlaybackAsync()); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopAudioPlayback();
            if (!_closing) ActionNotice.Text = "Audio playback failed. Check your audio output and try again.";
        }
    }

    private async Task ToggleAudioPlaybackAsync()
    {
        if (_closing || _acting || _audioLoading || !IsReading || _historyAudio is null || _opened?.Entry.PersistedRecordId is not { } id) return;
        if (_audioPlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _audioPlayer.Pause();
            SetAudioPlayButton(false);
            return;
        }
        if (CanPlayAudio?.Invoke() != true)
        {
            ActionNotice.Text = "Finish the current recording or operation before playing audio.";
            return;
        }
        var generation = ++_playbackGeneration;
        bool Current() => !_closing && generation == _playbackGeneration && IsReading && _opened?.Entry.PersistedRecordId == id;
        _audioLoading = true;
        SetAudioPlayButton(false);
        try
        {
            StopReadback();
            if (PrepareAudioPlayback is { } prepare) await prepare();
            if (!Current() || CanPlayAudio?.Invoke() != true) return;
            if (_audioPlayer is not null)
            {
                var session = _audioPlayer.PlaybackSession;
                if (session.NaturalDuration > TimeSpan.Zero && session.Position >= session.NaturalDuration)
                    session.Position = TimeSpan.Zero;
                _audioPlayer.Play();
                return;
            }
            var audio = _historyAudio;
            var path = await Task.Run(() =>
            {
                var record = audio.Records.FirstOrDefault(record => record.Id == id);
                return record is null ? null : audio.ResolveAudioPath(record.AudioFileName);
            });
            if (!Current()) return;
            if (path is null) throw new FileNotFoundException("Saved audio is no longer available.");
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!Current() || CanPlayAudio?.Invoke() != true) return;
            var player = new MediaPlayer { AutoPlay = false };
            _audioPlayer = player;
            _audioSource = MediaSource.CreateFromStorageFile(file);
            player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_audioPlayer, player) || _closing) return;
                if (CanPlayAudio?.Invoke() != true) { StopAudioPlayback(); return; }
                try { player.Play(); UpdateAudioPosition(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { StopAudioPlayback(); ActionNotice.Text = "The recording could not be played. Check your audio output."; }
            });
            player.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_audioPlayer, player) || _closing) return;
                try
                {
                    player.Pause();
                    player.PlaybackSession.Position = TimeSpan.Zero;
                    UpdateAudioPosition();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { StopAudioPlayback(); }
            });
            player.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_audioPlayer, player) || _closing) return;
                StopAudioPlayback();
                ActionNotice.Text = "Audio playback failed. Check your audio output or try again.";
            });
            player.Source = _audioSource;
            _audioTimer ??= DispatcherQueue.CreateTimer();
            _audioTimer.Interval = TimeSpan.FromMilliseconds(200);
            _audioTimer.Tick -= AudioTimer_Tick;
            _audioTimer.Tick += AudioTimer_Tick;
            _audioTimer.Start();
            ActionNotice.Text = "";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Current())
            {
                StopAudioPlayback();
                ActionNotice.Text = "The saved audio could not be played. Check the file and audio output, then try again.";
            }
        }
        finally
        {
            if (Current()) { _audioLoading = false; UpdateAudioPosition(); }
        }
    }

    private void AudioTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => UpdateAudioPosition();

    private void UpdateAudioPosition()
    {
        if (_closing) return;
        try
        {
            var session = _audioPlayer?.PlaybackSession;
            var duration = session?.NaturalDuration ?? TimeSpan.Zero;
            var position = session?.Position ?? TimeSpan.Zero;
            _updatingAudioPosition = true;
            AudioTimeline.Maximum = Math.Max(1, duration.TotalSeconds);
            AudioTimeline.Value = Math.Clamp(position.TotalSeconds, 0, AudioTimeline.Maximum);
            AudioTimeline.IsEnabled = duration > TimeSpan.Zero && session?.CanSeek == true;
            AudioPosition.Text = $"{(int)position.TotalMinutes}:{position.Seconds:00} / {(int)duration.TotalMinutes}:{duration.Seconds:00}";
            SetAudioPlayButton(session?.PlaybackState == MediaPlaybackState.Playing);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StopAudioPlayback();
            ActionNotice.Text = "The audio device became unavailable. Try playback again.";
        }
        finally { _updatingAudioPosition = false; }
    }

    private void AudioTimeline_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingAudioPosition || _audioPlayer is null || !AudioTimeline.IsEnabled) return;
        try { _audioPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { ActionNotice.Text = "Could not seek in this recording. Try playback again."; }
    }
}
