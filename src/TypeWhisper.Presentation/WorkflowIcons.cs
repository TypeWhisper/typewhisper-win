namespace TypeWhisper.Presentation;

/// <summary>Shared workflow icon choices and fallback for older or unknown values.</summary>
public static class WorkflowIcons
{
    /// <summary>The selectable icon identifiers and accessible names.</summary>
    public static IReadOnlyList<(string Id, string Label)> All { get; } =
    [
        ("workflow", Loc.T("Workflow")), ("correction", Loc.T("Edit")), ("text", Loc.T("Text")), ("file", Loc.T("Document")),
        ("dictionary", Loc.T("Words")), ("packs", Loc.T("Collection")), ("folder", Loc.T("Folder")), ("library", Loc.T("Library")),
        ("speech-history", Loc.T("Conversation")), ("microphone", Loc.T("Microphone")), ("speaker", Loc.T("Speaker")),
        ("recorder", Loc.T("Music")), ("play", Loc.T("Play")), ("history", Loc.T("Clock")), ("calendar", Loc.T("Calendar")),
        ("home", Loc.T("Home")), ("desktop", Loc.T("Desktop")), ("laptop", Loc.T("Laptop")), ("phone", Loc.T("Phone")),
        ("keyboard", Loc.T("Keyboard")), ("chip", Loc.T("Code")), ("plugin", Loc.T("Plugin")), ("layout", Loc.T("Layout")),
        ("stats", Loc.T("Chart")), ("speed", Loc.T("Speed")), ("trophy", Loc.T("Trophy")), ("flame", Loc.T("Flame")),
        ("sparkle", Loc.T("Sparkle")), ("lock", Loc.T("Private")), ("check", Loc.T("Check"))
    ];

    /// <summary>Returns a supported icon or the default workflow symbol.</summary>
    public static string Normalize(string? icon) => All.Any(choice => choice.Id == icon) ? icon! : "workflow";
}
