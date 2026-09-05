namespace TypeWhisper.WinUIPrototype;

// Fictional fixtures, not an inventory of installed plugins or an SDK contract.
public sealed record PrototypePlugin(string Id, string Title, string Description, string IconKind,
    string Category, string Permissions, string Version, string MinimumHostVersion)
{
    internal static readonly Version DemoHostVersion = new(1, 1);
    public bool Enabled { get; init; } = true;
    public bool RequiresConnection { get; init; }
    public bool Connected { get; init; }
    public string Preference { get; init; } = "balanced";
    public string Language { get; init; } = "auto";
    public bool Compatible => System.Version.Parse(MinimumHostVersion) <= DemoHostVersion;
    public string Status => !Compatible ? "Host update required" : !Enabled ? "Disabled"
        : RequiresConnection && !Connected ? "Setup needed" : "Enabled";
    public bool NeedsAttention => !Compatible || Enabled && RequiresConnection && !Connected;

    internal static IReadOnlyList<PrototypePlugin> Samples =>
    [
        new("local", "Local transcription", "Turn speech into text on this device.", "microphone", "Transcription · On-device",
            "Audio from recordings you choose to transcribe. No network access in this example.", "1.1.0", "1.1"),
        new("cloud", "Cloud transcription", "Use a connected transcription provider.", "plugin", "Transcription · Cloud",
            "Audio sent to your chosen provider when you start transcription. Network access required.", "1.1.0", "1.1") { RequiresConnection = true },
        new("markdown", "Markdown export", "Keep headings and lists when exporting text.", "file", "Export · Local",
            "The transcript you select and clipboard access when you explicitly copy it.", "1.0.0", "1.1") { Enabled = false },
        new("formatter", "Text formatter preview", "An example of a plugin requiring a newer host.", "workflow", "Text processing · Preview",
            "Only the text you explicitly send to a workflow.", "2.0.0", "2.0") { Enabled = false }
    ];
}
