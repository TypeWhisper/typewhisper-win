namespace TypeWhisper.WinUI;

public sealed record Command(
    string Category,
    string IconKind,
    string Title,
    string Subtitle,
    string Shortcut,
    string Detail,
    bool IsActive = false);
