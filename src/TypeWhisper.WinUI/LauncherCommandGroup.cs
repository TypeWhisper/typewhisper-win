using Microsoft.UI.Xaml;

namespace TypeWhisper.WinUI;

public sealed record LauncherCommandGroup(string Title, IReadOnlyList<Command> Items, bool HasDivider)
{
    public Visibility TitleVisibility => Title == "Suggestions" ? Visibility.Collapsed : Visibility.Visible;
    public Thickness HeaderPadding => Title == "Suggestions" ? new(0) : new(10, 8, 0, 4);
    public Thickness Separator => new(0, HasDivider ? 1 : 0, 0, 0);
    public Thickness HeaderMargin => new(0, HasDivider ? 12 : 0, 0, 4);
}
