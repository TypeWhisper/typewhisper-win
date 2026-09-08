using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed record LauncherCommandGroup(string Title, IReadOnlyList<Command> Items, bool HasDivider)
{
    public Thickness Separator => new(0, HasDivider ? 1 : 0, 0, 0);
    public Thickness HeaderMargin => new(0, HasDivider ? 12 : 0, 0, 4);
}
