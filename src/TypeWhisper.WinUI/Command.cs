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
    public int IconColumn => IsSuggestionsToggle ? 2 : 0;
    public Microsoft.UI.Xaml.GridLength IconWidth => new(IsSuggestionsToggle ? 0 : 36);
    public Microsoft.UI.Xaml.Thickness LabelMargin => IsSuggestionsToggle ? new(0) : new(10, 0, 12, 0);
    public double TitleSize => IsSuggestionsToggle ? 11 : 13;
    public Microsoft.UI.Xaml.Visibility SubtitleVisibility => IsSuggestionsToggle ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public bool IsSuggestionsToggle { get; init; }
    public string? WorkflowId { get; init; }
    public string Key => WorkflowId is null ? Title : "workflow:" + WorkflowId;
    public string PinLabel => IsPinned ? "Pinned" : "";
}
