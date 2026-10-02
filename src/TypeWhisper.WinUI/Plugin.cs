namespace TypeWhisper.WinUI;

// Display data for installed and catalog plugins, not an SDK contract.
public sealed record Plugin(string Id, string Title, string Description, string IconKind,
    string Categories, string Permissions, string Version, string MinimumHostVersion)
{
    public bool Enabled { get; init; } = true;
    public bool RuntimeCanToggle { get; init; }
    public string Status { get; init; } = "";
    public string? Author { get; init; }
    public bool IsLocal { get; init; }
}
