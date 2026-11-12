namespace TypeWhisper.WinUI;

// What the tray says while the Recorder captures audio. The tray icon carries a recording mark and its tooltip names the
// sources for as long as the recording runs, whichever way it was started; a recording that another app started through
// the local API also shows a notification.
internal static class RecorderIndicator
{
    /// <summary>The tray tooltip status for a running recording.</summary>
    internal static string Status(bool microphone, bool systemAudio) => (microphone, systemAudio) switch
    {
        (true, true) => Loc.T("Recording microphone and system audio"),
        (false, true) => Loc.T("Recording system audio"),
        _ => Loc.T("Recording microphone"),
    };

    /// <summary>The notification title for a recording that another app started.</summary>
    internal static string ApiStartedTitle => Loc.T("Another app started a recording");

    /// <summary>The notification text for a recording that another app started.</summary>
    internal static string ApiStartedMessage(bool microphone, bool systemAudio) => (microphone, systemAudio) switch
    {
        (true, true) => Loc.T("TypeWhisper is recording the microphone and system audio. Stop it from the tray menu or in Recorder."),
        (false, true) => Loc.T("TypeWhisper is recording the system audio. Stop it from the tray menu or in Recorder."),
        _ => Loc.T("TypeWhisper is recording the microphone. Stop it from the tray menu or in Recorder."),
    };
}
