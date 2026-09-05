namespace TypeWhisper.WinUIPrototype;

// UI fixtures only; these do not load or execute production workflows.
public sealed record PrototypeWorkflow(string Id, string Title, string Description, string IconKind,
    string Instruction, string ExampleInput, string ExampleOutput)
{
    public string ProviderId { get; init; } = "none";
    public string ModelId { get; init; } = "";
    public string OutputTarget { get; init; } = "preview";
    public bool HasExample => !string.IsNullOrEmpty(ExampleOutput);

    public static IReadOnlyList<PrototypeWorkflow> Samples { get; } =
    [
        new("format", "Smart Formatting", "Clean up punctuation and paragraphs while keeping your voice.", "workflow",
            "Improve readability without changing the meaning or adding new information.",
            "hallo team danke für euer feedback der neue quick launch sieht schon richtig gut aus bitte prüft morgen noch die tastaturnavigation und die darstellung auf dem zweiten monitor danach besprechen wir die nächsten schritte",
            "Hallo Team,\n\nvielen Dank für euer Feedback. Der neue Quick Launch sieht schon richtig gut aus.\n\nBitte prüft morgen noch die Tastaturnavigation und die Darstellung auf dem zweiten Monitor. Danach besprechen wir die nächsten Schritte."),
        new("summary", "Summary", "Bring a longer thought down to its essentials.", "file",
            "Summarize the key points clearly and concisely. Keep the original language.",
            "Wir haben heute die History und den Recorder getestet. Die Filter nach Gerät und Eintragstyp funktionieren. Abgeschlossene Aufnahmen erscheinen in der History und verworfene Sitzungen nicht. Als Nächstes möchten wir die Workflows direkt im Quick Launch ausprobieren. Dabei sollen wir den Ausgangstext prüfen können, bevor wir das Ergebnis kopieren.",
            "History und Recorder sind getestet: Gerätefilter und die Übernahme abgeschlossener Aufnahmen funktionieren. Als Nächstes werden Workflows mit prüfbarem Ausgangstext und Ergebnis direkt in den Quick Launch integriert."),
        new("translation", "Translation", "Turn German notes into natural English.", "dictionary",
            "Translate into English, preserving tone and meaning. Return only the translation.",
            "Können wir den Termin auf Donnerstag verschieben? Ich möchte vorher noch die neue Version auf zwei Monitoren testen. Vielen Dank für eure Geduld!",
            "Could we move the meeting to Thursday? I'd like to test the new version on two monitors first. Thanks for your patience!"),
        new("checklist", "Checklist", "Pull the next actions out of your notes.", "check",
            "Extract concrete action items as a checklist. Do not invent tasks or deadlines.",
            "Vor dem nächsten Test müssen wir den Prototyp starten, die History filtern und eine Aufnahme abschließen. Dann wollen wir die Geräteauswahl mit der Tastatur bedienen. Zum Schluss machen wir einen Screenshot und sammeln das Feedback.",
            "☐ Prototyp starten\n☐ History filtern\n☐ Eine Aufnahme abschließen\n☐ Geräteauswahl mit der Tastatur bedienen\n☐ Screenshot erstellen\n☐ Feedback sammeln")
    ];
}
