namespace TypeWhisper.WinUI;

public sealed record PrototypeCommand(
    string Category,
    string IconKind,
    string Title,
    string Subtitle,
    string Shortcut,
    string Detail,
    bool IsActive = false);
