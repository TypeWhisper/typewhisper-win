namespace TypeWhisper.WinUI;

public sealed partial class MainWindow
{
    private static readonly string[] SettingsDestinations =
        ["General", "Shortcuts", "Dictation", "Audio", "Recorder", "Files & recovery", "Appearance", "Privacy", "Advanced", "Premium", "Account & about"];

    private static IEnumerable<Command> WorkspaceCommands => new Command[]
    {
        Destination("Setup wizard", "sparkle", "Set up TypeWhisper · microphone, hotkey and dictation model", "setup"),
        Destination("Corrections", "correction", "Dictionary · preferred spellings and misheard variants", "corrections"),
        Destination("Term packs", "packs", "Dictionary · enable vocabulary collections", "packs"),
        Destination("Watch folder", "folder", "Files · automatic folder transcription", "watch"),
        Destination("Recordings", "library", "Recorder · saved audio library", "recordings"),
        Destination("Dictation history", "speech-history", "History · dictated transcriptions", "history-dictation"),
        Destination("Recording history", "wave-history", "History · transcribed recordings", "history-recording"),
        Destination("Overlay editor", "layout", "Appearance · customize the recording overlay", "settings:Overlay editor")
    }.Concat(SettingsDestinations.Select(category => Destination(category + " settings", SettingsIcon(category), "Settings · " + category, "settings:" + category)));

    private static string SettingsIcon(string category) => category switch
    {
        "General" => "home",
        "Shortcuts" => "keyboard",
        "Dictation" => "signal",
        "Audio" => "speaker",
        "Recorder" => "record-settings",
        "Files & recovery" => "restore",
        "Appearance" => "desktop",
        "Privacy" => "lock",
        "Advanced" => "chip",
        "Premium" => "sparkle",
        "Account & about" => "info",
        _ => "settings"
    };

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
            case "setup": OpenSetup(); break;
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
