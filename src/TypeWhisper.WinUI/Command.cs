namespace TypeWhisper.WinUI;

public sealed record Command(
    string Category,
    string IconKind,
    string Title,
    string Subtitle,
    string Shortcut,
    string Detail,
    bool IsActive = false,
    bool IsPinned = false)
{
    public string? WorkflowId { get; init; }
    public string Key => WorkflowId is null ? Title : "workflow:" + WorkflowId;
    public string PinLabel => IsPinned ? "Pinned" : "";
}
