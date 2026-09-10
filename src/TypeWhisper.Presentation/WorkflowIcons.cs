namespace TypeWhisper.Presentation;

/// <summary>Shared workflow icon choices and fallback for older or unknown values.</summary>
public static class WorkflowIcons
{
    /// <summary>The selectable icon identifiers and accessible names.</summary>
    public static IReadOnlyList<(string Id, string Label)> All { get; } =
    [
        ("workflow", "Workflow"), ("correction", "Edit"), ("text", "Text"), ("file", "Document"),
        ("dictionary", "Words"), ("packs", "Collection"), ("folder", "Folder"), ("library", "Library"),
        ("speech-history", "Conversation"), ("microphone", "Microphone"), ("speaker", "Speaker"),
        ("recorder", "Music"), ("play", "Play"), ("history", "Clock"), ("calendar", "Calendar"),
        ("home", "Home"), ("desktop", "Desktop"), ("laptop", "Laptop"), ("phone", "Phone"),
        ("keyboard", "Keyboard"), ("chip", "Code"), ("plugin", "Plugin"), ("layout", "Layout"),
        ("stats", "Chart"), ("speed", "Speed"), ("trophy", "Trophy"), ("flame", "Flame"),
        ("sparkle", "Sparkle"), ("lock", "Private"), ("check", "Check")
    ];

    /// <summary>Returns a supported icon or the default workflow symbol.</summary>
    public static string Normalize(string? icon) => All.Any(choice => choice.Id == icon) ? icon! : "workflow";
}
