namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private static readonly string[] SettingsDestinations =
        ["General", "Shortcuts", "Dictation", "Audio", "Recorder", "Files & recovery", "Appearance", "Privacy", "Advanced", "Premium", "Account & about"];

    private static IEnumerable<Command> WorkspaceCommands => new Command[]
    {
        Destination("Corrections", "dictionary", "Dictionary · preferred spellings and misheard variants", "corrections"),
        Destination("Term packs", "dictionary", "Dictionary · enable vocabulary collections", "packs"),
        Destination("Watch folder", "file", "Files · automatic folder transcription", "watch"),
        Destination("Recordings", "recorder", "Recorder · saved audio library", "recordings"),
        Destination("Dictation history", "history", "History · dictated transcriptions", "history-dictation"),
        Destination("Recording history", "history", "History · transcribed recordings", "history-recording"),
        Destination("Discover plugins", "plugin", "Integrations · browse the marketplace", "discover"),
        Destination("Overlay editor", "desktop", "Appearance · customize the recording overlay", "settings:Overlay editor")
    }.Concat(SettingsDestinations.Select(category => Destination(category + " settings", "settings", "Settings · " + category, "settings:" + category)));

    private static Command Destination(string title, string icon, string subtitle, string route) =>
        new("Workspace", icon, title, subtitle, "", "Open " + title) { Route = route };

    private void OpenWorkspaceCommand(string route)
    {
        if (route.StartsWith("settings:", StringComparison.Ordinal))
        {
            OpenSettings();
            _settingsWindow?.ShowCategory(route[9..]);
            return;
        }
        switch (route)
        {
            case "corrections": OpenLexicon(section: "corrections"); break;
            case "packs": OpenLexicon(section: "packs"); break;
            case "watch": OpenFileTranscription(); _fileTranscription?.ShowWatchFolder(); break;
            case "recordings": OpenRecorder(); RecorderView.ShowLibrary(true); break;
            case "history-dictation": OpenHistory(); HistoryView.SelectKind(HistoryEntryKind.Dictation); break;
            case "history-recording": OpenHistory(); HistoryView.SelectKind(HistoryEntryKind.Recording); break;
            case "discover": OpenMarketplace(); break;
        }
    }
}
