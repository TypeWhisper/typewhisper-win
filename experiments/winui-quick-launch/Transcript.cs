namespace TypeWhisper.WinUI;

// Presentation adapter: display labels never become canonical timestamps,
// durations, device identities or language identifiers in the history model.
public sealed record Transcript(HistoryEntry Entry, string Time)
{
    public string Title => Entry.Content.Title;
    public string Text => Entry.Content.Transcript?.RenderedDocument
        ?? Entry.Content.Transcript?.FinalText ?? string.Empty;
    public string Preview => Entry.HasTranscript ? Text.Replace("\n", " ")
        : Entry.LocalState.IsSample ? "Demo session · no audio captured or transcript generated" : "No transcript yet";
    public string IconKind => Entry.Content.Kind == HistoryEntryKind.Recording ? "signal" : "file";
    public string KindLabel => Entry.Content.Kind switch
    {
        HistoryEntryKind.Dictation => "Dictation",
        HistoryEntryKind.Recording => "Recording",
        HistoryEntryKind.ImportedFile => "Imported file",
        _ => "Entry"
    };
    public string DeviceLabel => Entry.Content.Origin.DeviceName ?? Entry.Content.Origin.Platform;
    public string AudioDescription => Entry.AudioAvailability switch
    {
        AudioAvailability.RemoteOnly => "Audio on another device · sample reference, no download in this preview",
        AudioAvailability.LocalOnly or AudioAvailability.LocalAndRemote => "Audio available on this device",
        AudioAvailability.Unavailable => "Audio is currently unavailable on this device",
        _ => Entry.LocalState.IsSample ? "Sample entry · no audio file created" : "No audio attached"
    };
    public int WordCount => Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    public string Duration
    {
        get
        {
            var seconds = (long)Entry.Content.DurationSeconds;
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
    public string Language => Entry.Content.LanguageCode switch
    {
        "de" => "German",
        "en" => "English",
        null => "Unknown language",
        var code => code
    };
    public string Metadata => $"{KindLabel} · {DeviceLabel} · {Duration}"
        + (Entry.HasTranscript ? $" · {WordCount} words · {Language}" : " · No transcript");
}
