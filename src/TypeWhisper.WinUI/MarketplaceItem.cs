namespace TypeWhisper.WinUI;

public sealed record MarketplaceItem(Plugin Plugin, string Publisher)
{
    public IReadOnlyList<string> CategoriesIds { get; init; } = [];
    public bool Installed { get; init; }
    public bool Supported { get; init; } = true;
    public bool UpdateAvailable { get; init; }
    public bool PendingRestart { get; init; }
    /// <summary>Why machine policy refuses installing this entry, or null.</summary>
    public string? Refusal { get; init; }
    public string Title => Plugin.Title;
    public string Description => Plugin.Description;
    public string IconKind => Plugin.IconKind;
    public string Categories => Plugin.Categories;
    public string Status => PendingRestart ? Loc.T("Restart required") : !Supported ? Loc.T("Not compatible") : Refusal is not null && !Installed ? Loc.T("Not allowed") : UpdateAvailable ? Loc.T("Update available") : Installed ? Loc.T("Installed") : Loc.T("Available");
}
