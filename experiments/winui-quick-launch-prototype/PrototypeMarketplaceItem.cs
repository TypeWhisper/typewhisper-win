namespace TypeWhisper.WinUIPrototype;

// Entirely fictional catalog and signature states. No remote registry is queried.
public sealed record PrototypeMarketplaceItem(PrototypePlugin Plugin, string Publisher, bool Trusted = true)
{
    public bool Installed { get; init; }
    public string Title => Plugin.Title;
    public string Description => Plugin.Description;
    public string IconKind => Plugin.IconKind;
    public string Category => Plugin.Category;
    public string Status => Installed ? "Installed" : !Trusted ? "Signature unavailable"
        : !Plugin.Compatible ? "Host update required" : "Available";

    internal static IReadOnlyList<PrototypeMarketplaceItem> Samples =>
    [
        new(new("studio", "Studio transcription", "Transcribe interviews and longer recordings with a cloud provider.", "microphone", "Transcription · Cloud",
            "Audio from recordings you select, sent to a cloud provider. Requires network access and a provider connection.", "1.1.0", "1.1") { RequiresConnection = true }, "Studio Labs · fictional publisher"),
        new(new("plain-export", "Plain text export", "Copy a clean transcript without headings or Markdown syntax.", "file", "Export · Local",
            "The transcript you select and clipboard access when you explicitly copy it. No network access.", "1.0.0", "1.1") { Preference = "plain" }, "Notebook Tools · fictional publisher"),
        new(PrototypePlugin.Samples.First(plugin => plugin.Id == "local"), "TypeWhisper · demo listing"),
        new(PrototypePlugin.Samples.First(plugin => plugin.Id == "markdown"), "TypeWhisper · demo listing"),
        new(new("next-speech", "Next speech preview", "Preview the unavailable state for a plugin built for a newer host.", "signal", "Transcription · Preview",
            "Audio from recordings you select. Illustrative permission request only.", "2.0.0", "2.0"), "Future Audio · fictional publisher"),
        new(new("unsigned", "Unsigned transcription example", "See how the catalog handles a package without a trusted signature.", "plugin", "Transcription · Test fixture",
            "Audio and network access. This example cannot be installed.", "1.0.0", "1.1"), "Unknown publisher · test fixture", false)
    ];
}
