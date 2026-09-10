using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

public sealed partial class HistoryView
{
    internal Func<string, string?, Task<SpokenFeedbackResult>>? ReadTranscript { get; set; }
    internal Func<Task>? StopReading { get; set; }
    private Task<SpokenFeedbackResult>? _readback;
    private long _readbackRevision;
    private bool _readbackStarting;
    private bool ReadbackActive => _readbackStarting || _readback is { IsCompleted: false };

    internal void StopReadback()
    {
        ++_readbackRevision;
        if (_readback is { IsCompleted: false } && StopReading is { } stop)
            _ = TrackWriteAsync(stop());
    }

    private void UpdateReadbackButton()
    {
        var playing = _readback is { IsCompleted: false };
        ReadAloudButton.Content = playing ? "Stop reading · P" : "Read aloud · P";
        AutomationProperties.SetName(ReadAloudButton, playing ? "Stop reading transcript" : "Read transcript aloud");
        ReadAloudButton.Visibility = IsReading && _opened?.Entry.HasTranscript == true && ReadTranscript is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ReadAloud_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _acting || _loading || !IsReading || _opened?.Entry.HasTranscript != true || ReadTranscript is null) return;
        var revision = ++_readbackRevision;
        var id = _opened.Entry.RecordId;
        try
        {
            if (_readback is { IsCompleted: false })
            {
                if (StopReading is { } stop) await TrackWriteAsync(stop());
                if (!_closing && IsReading && _opened?.Entry.RecordId == id && revision == _readbackRevision)
                    ActionNotice.Text = "Read-back stopped.";
                return;
            }
            // Capture the displayed entry; reading never changes the last-dictation snapshot.
            StopAudioPlayback();
            _readbackStarting = true;
            try { _readback = ReadTranscript(_opened.Text, _opened.Entry.Content.LanguageCode); }
            finally { _readbackStarting = false; }
            UpdateReadbackButton();
            ActionNotice.Text = "Reading transcript… Press P to stop.";
            var result = await TrackWriteAsync(_readback);
            if (!_closing && IsReading && _opened?.Entry.RecordId == id && revision == _readbackRevision)
                ActionNotice.Text = result.Message ?? (result.Status == SpokenFeedbackStatus.Completed
                    ? "Finished reading transcript." : "Read-back stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_closing && IsReading && _opened?.Entry.RecordId == id && revision == _readbackRevision)
                ActionNotice.Text = "This transcript could not be read aloud. Check the voice and audio output.";
        }
        finally { if (!_closing) UpdateReadbackButton(); }
    }
}
