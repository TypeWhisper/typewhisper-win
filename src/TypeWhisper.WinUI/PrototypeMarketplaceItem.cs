namespace TypeWhisper.WinUI;

public sealed record PrototypeMarketplaceItem(PrototypePlugin Plugin, string Publisher)
{
    public bool Installed { get; init; }
    public bool Supported { get; init; } = true;
    public bool UpdateAvailable { get; init; }
    public bool PendingRestart { get; init; }
    public string Title => Plugin.Title;
    public string Description => Plugin.Description;
    public string IconKind => Plugin.IconKind;
    public string Category => Plugin.Category;
    public string Status => PendingRestart ? "Restart required" : !Supported ? "Not compatible" : UpdateAvailable ? "Update available" : Installed ? "Installed" : "Available";
}
