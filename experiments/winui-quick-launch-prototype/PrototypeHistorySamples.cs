namespace TypeWhisper.WinUIPrototype;

internal static class PrototypeHistorySamples
{
    internal static readonly PrototypeTranscript[] Entries =
    [
        Sample(1, "Gedanken zum neuen Quick Launch", "Today · 09:42", 84, "de",
            "Der Quick Launch soll der Ausgangspunkt für die tägliche Arbeit sein. Ich möchte meine letzten Transkripte wiederfinden, eine Aufnahme starten oder einen Workflow ausführen, ohne dafür die Einstellungen öffnen zu müssen.\n\n"
            + "Die Oberfläche bleibt dabei ruhig: eine Suche, verständliche Einträge und die Aktionen, die gerade wirklich helfen. Angepinnte Befehle bleiben leicht erreichbar. Während einer Aufnahme darf die Palette geöffnet werden, ohne dass das Overlay verschwindet.\n\n"
            + "Für die History wünsche ich mir eine einfache Liste mit einer kurzen Vorschau. Wenn ich einen Eintrag öffne, möchte ich den vollständigen Text lesen, einzelne Stellen auswählen oder alles kopieren. Die Suche sollte auch Wörter finden, die mitten im Transkript stehen.\n\n"
            + "Wichtig ist außerdem die Navigation mit der Tastatur. Zurück bringt mich an dieselbe Stelle der Liste. Escape darf keine laufende Aufnahme abbrechen. Auch längere Texte brauchen genug Platz und einen gut erreichbaren Scrollbalken.\n\n"
            + "Die technischen Details müssen nicht ständig sichtbar sein. Modell, Sprache und weitere Informationen können später bei Bedarf eingeblendet werden. Im Mittelpunkt steht der Text, den ich gerade gesprochen habe."),
        Sample(2, "Antwort an das Design-Team", "Today · 09:18", 38, "de",
            "Danke für eure Vorschläge. Die kompakte Ansicht gefällt mir bereits sehr gut. Bitte achtet noch darauf, dass Suchtext und Platzhalter dieselbe Grundlinie haben.\n\nDie Abstände sollten auch bei einer Skalierung von 200 Prozent stimmen. Lasst uns die nächste Version gemeinsam auf zwei verschiedenen Monitoren ansehen."),
        Sample(3, "A quieter recording experience", "Today · 08:56", 52, "en",
            "The minimal recording indicator should stay out of the way. It only needs to reassure me that recording is active and react when I speak.\n\nFor longer sessions, I would switch to the standard overlay with a timer and optional live text. The transcription should grow upward while the recording controls remain anchored in place."),
        Sample(4, "Notizen für morgen", "Yesterday · 18:07", 26, "de",
            "Morgen zuerst die neue History ausprobieren. Danach den Recorder und die Plugin-Einstellungen durchgehen.\n\nFür den Test brauchen wir einen kurzen Text, einen langen Text und einen Suchbegriff, der nicht vorkommt. Außerdem einmal alles ausschließlich mit der Tastatur bedienen."),
        Sample(5, "Meeting follow-up", "Yesterday · 15:30", 44, "en",
            "We agreed to keep the first prototype focused on navigation and readability. No account connection or real transcription service is required for this round.\n\nThe next review should cover search, opening a result, copying text, and returning to the previous selection. Please bring one example where the transcript spans several paragraphs."),
        Sample(6, "Eine kurze Erinnerung", "Yesterday · 11:12", 8, "de",
            "Beim nächsten Einkauf Kaffee, Hafermilch und frisches Brot mitbringen."),
        Sample(7, "Workflow-Idee", "Sep 2 · 16:05", 32, "de",
            "Ein Workflow könnte aus diktierten Stichpunkten eine verständliche Nachricht machen. Vor dem Ausführen möchte ich sehen, welcher Anbieter verwendet wird.\n\nDas Ergebnis soll ich überprüfen können, bevor ich es in eine andere Anwendung übernehme.")
    ];

    private static PrototypeTranscript Sample(int number, string title, string timeLabel,
        double seconds, string language, string text)
    {
        // Explicit fixture timestamps, independent of the localized labels above.
        var (day, hour, minute) = number switch
        {
            1 => (4, 9, 42), 2 => (4, 9, 18), 3 => (4, 8, 56),
            4 => (3, 18, 7), 5 => (3, 15, 30), 6 => (3, 11, 12), _ => (2, 16, 5)
        };
        var createdAt = new DateTimeOffset(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(2)).ToUniversalTime();
        var origin = number switch
        {
            3 or 6 => PrototypeHistoryDevices.Phone,
            5 => PrototypeHistoryDevices.Mac,
            _ => PrototypeHistoryDevices.ThisPc
        };
        var source = origin.Platform switch { "iOS" => "iPhone", "macOS" => "mac", _ => "windows" };
        var entry = new PrototypeHistoryEntry(
            Guid.Parse($"00000000-0000-4000-8000-{number:000000000000}"),
            new PrototypeHistoryContent(createdAt, createdAt, origin, source,
                number == 5 ? PrototypeHistoryEntryKind.Recording : PrototypeHistoryEntryKind.Dictation,
                title, seconds, PrototypeHistoryProcessingState.Ready,
                new PrototypeHistoryTranscript(text, text), language),
            new PrototypeHistoryInbox(createdAt),
            number == 5 ? new PrototypeHistoryAudio(createdAt, "audio/mp4", 1234,
                "assets/history/prototype-mac-recording/audio.m4a", new string('a', 64)) : null)
        {
            LocalState = new PrototypeHistoryLocalState(IsSample: true)
        };
        entry.Validate();
        return new PrototypeTranscript(entry, timeLabel);
    }
}
