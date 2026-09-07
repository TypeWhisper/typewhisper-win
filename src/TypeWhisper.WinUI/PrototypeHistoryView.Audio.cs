using System.Diagnostics;
using Microsoft.UI.Xaml;
using TypeWhisper.Core.Interfaces;

namespace TypeWhisper.WinUI;

public sealed partial class PrototypeHistoryView
{
    private IHistoryAudioService? _historyAudio;
    private int _audioAvailabilityGeneration;

    private void UpdateAudioCleanupNotice()
    {
        var error = _historyAudio?.AudioCleanupError;
        AudioCleanupNotice.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        AudioCleanupText.Text = error ?? "";
        RetryAudioCleanupButton.IsEnabled = !_acting && !_closing;
    }

    private void RefreshAudioAvailability()
    {
        if (_closing) return;
        var generation = ++_audioAvailabilityGeneration;
        AudioActions.Visibility = Visibility.Collapsed;
        var id = _opened?.Entry.PersistedRecordId;
        if (_historyAudio is not { } audio || id is null)
        {
            AudioAvailabilityText.Text = "No audio was saved with this entry.";
            return;
        }
        AudioAvailabilityText.Text = "Checking saved audio…";
        _ = TrackWriteAsync(ReadAudioAvailabilityAsync(audio, id, generation));
    }

    private async Task ReadAudioAvailabilityAsync(IHistoryAudioService audio, string id, int generation)
    {
        bool Current() => !_closing && generation == _audioAvailabilityGeneration && _opened?.Entry.PersistedRecordId == id;
        try
        {
            var availability = await Task.Run(() =>
            {
                var record = audio.Records.FirstOrDefault(record => record.Id == id);
                var hasReference = !string.IsNullOrWhiteSpace(record?.AudioFileName);
                return (HasReference: hasReference, Path: hasReference ? audio.ResolveAudioPath(record!.AudioFileName) : null);
            });
            if (!Current()) return;
            if (!availability.HasReference)
                AudioAvailabilityText.Text = "No audio was saved with this entry.";
            else if (availability.Path is null)
                AudioAvailabilityText.Text = "The saved audio is missing or could not be verified. The transcript is still available.";
            else
            {
                AudioAvailabilityText.Text = "Audio saved on this device. It is deleted with this history entry.";
                AudioActions.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (Current()) AudioAvailabilityText.Text = "The saved audio could not be read. The transcript is still available.";
        }
        if (Current()) PlayAudioButton.IsEnabled = ShowAudioButton.IsEnabled = !_acting;
    }

    private async void PlayAudio_Click(object sender, RoutedEventArgs e) => await TrackWriteAsync(OpenHistoryAudioAsync(false));
    private async void ShowAudio_Click(object sender, RoutedEventArgs e) => await TrackWriteAsync(OpenHistoryAudioAsync(true));

    private async Task OpenHistoryAudioAsync(bool folder)
    {
        if (_closing || _acting || _historyAudio is not { } audio || _opened?.Entry.PersistedRecordId is not { } id) return;
        _acting = true;
        PlayAudioButton.IsEnabled = ShowAudioButton.IsEnabled = false;
        UpdateBulkActions();
        try
        {
            var path = await Task.Run(() =>
            {
                var record = audio.Records.FirstOrDefault(record => record.Id == id);
                return record is null ? null : audio.ResolveAudioPath(record.AudioFileName);
            });
            if (_closing || _opened?.Entry.PersistedRecordId != id) return;
            if (path is null)
            {
                ActionNotice.Text = "The saved audio is no longer available. Your transcript was not changed.";
                return;
            }
            // The resolver accepts only the audio store's verified local WAV files.
            using var process = Process.Start(folder
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }
                : new ProcessStartInfo(path) { UseShellExecute = true });
            ActionNotice.Text = folder ? "Opened the audio folder." : "Opened audio in your default app.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing) ActionNotice.Text = "The audio could not be opened. Check your default audio app or try Show in folder.";
        }
        finally
        {
            _acting = false;
            if (!_closing) { RefreshAudioAvailability(); UpdateBulkActions(); UpdateAudioCleanupNotice(); }
        }
    }

    private async void RetryAudioCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _historyAudio is not { } audio) return;
        _acting = true;
        UpdateBulkActions(); UpdateAudioCleanupNotice();
        string? retryError = null;
        try { await TrackWriteAsync(Task.Run(audio.RetryAudioCleanup)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { retryError = "Audio cleanup could not finish. Try again after closing apps using the recording."; }
        finally
        {
            _acting = false;
            if (!_closing)
            {
                UpdateBulkActions(); UpdateAudioCleanupNotice(); await RefreshAsync();
                if (!_closing && retryError is not null)
                { AudioCleanupNotice.Visibility = Visibility.Visible; AudioCleanupText.Text = retryError; }
            }
        }
    }
}
