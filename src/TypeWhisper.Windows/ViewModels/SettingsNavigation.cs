using CommunityToolkit.Mvvm.ComponentModel;

namespace TypeWhisper.Windows.ViewModels;

public enum SettingsRoute
{
    Dashboard,
    Dictation,
    Shortcuts,
    FileTranscription,
    Recorder,
    History,
    Dictionary,
    Snippets,
    Workflows,
    Integrations,
    General,
    Appearance,
    Advanced,
    Premium,
    License,
    About
}

public enum SettingsGroup
{
    Overview,
    Capture,
    Library,
    AI,
    System
}

public enum SettingsPageKind
{
    PreferencePage,
    CollectionPage,
    GuidedEditorPage
}

public sealed record SettingsPageMetadata(
    SettingsPageKind Kind,
    double ContentWidth = 980,
    bool ShowsSummaryRow = true,
    bool UsesStickyActions = false);

public sealed partial class SettingsNavigationItem : ObservableObject
{
    public SettingsNavigationItem(SettingsRoute route, string title, string iconGlyph, string? badgeText = null)
    {
        Route = route;
        Title = title;
        IconGlyph = iconGlyph;
        BadgeText = badgeText;
    }

    public SettingsRoute Route { get; }
    public string Title { get; }
    public string IconGlyph { get; }
    public string? BadgeText { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class SettingsNavigationGroup
{
    public SettingsNavigationGroup(SettingsGroup group, string title, IReadOnlyList<SettingsNavigationItem> items)
    {
        Group = group;
        Title = title;
        Items = items;
    }

    public SettingsGroup Group { get; }
    public string Title { get; }
    public IReadOnlyList<SettingsNavigationItem> Items { get; }
}

public static class SettingsNavigationCatalog
{
    public static IReadOnlyList<SettingsNavigationGroup> Build(Func<string, string> text) =>
    [
        CreateGroup(SettingsGroup.Overview, text("SettingsGroup.Overview"),
        [
            new SettingsNavigationItem(SettingsRoute.Dashboard, text("Nav.Dashboard"), "\uE80F")
        ]),
        CreateGroup(SettingsGroup.Capture, text("SettingsGroup.Capture"),
        [
            new SettingsNavigationItem(SettingsRoute.Dictation, text("Nav.Dictation"), "\uE720"),
            new SettingsNavigationItem(SettingsRoute.Shortcuts, text("Nav.Shortcuts"), "\uE765"),
            new SettingsNavigationItem(SettingsRoute.FileTranscription, text("Nav.FileTranscription"), "\uE8A5"),
            new SettingsNavigationItem(SettingsRoute.Recorder, text("Nav.Recorder"), "\uE189")
        ]),
        CreateGroup(SettingsGroup.Library, text("SettingsGroup.Library"),
        [
            new SettingsNavigationItem(SettingsRoute.History, text("Nav.History"), "\uE81C"),
            new SettingsNavigationItem(SettingsRoute.Dictionary, text("Nav.Dictionary"), "\uE8D2"),
            new SettingsNavigationItem(SettingsRoute.Snippets, text("Nav.Snippets"), "\uE8C8")
        ]),
        CreateGroup(SettingsGroup.AI, text("SettingsGroup.AI"),
        [
            new SettingsNavigationItem(SettingsRoute.Workflows, text("Nav.Workflows"), "\uE8F1"),
            new SettingsNavigationItem(SettingsRoute.Integrations, text("Nav.Plugins"), "\uE943")
        ]),
        CreateGroup(SettingsGroup.System, text("SettingsGroup.System"),
        [
            new SettingsNavigationItem(SettingsRoute.General, text("Nav.General"), "\uE713"),
            new SettingsNavigationItem(SettingsRoute.Appearance, text("Nav.Appearance"), "\uE790"),
            new SettingsNavigationItem(SettingsRoute.Advanced, text("Nav.Advanced"), "\uE9CE"),
            new SettingsNavigationItem(SettingsRoute.Premium, text("Nav.Premium"), "\uE735"),
            new SettingsNavigationItem(SettingsRoute.License, text("Nav.License"), "\uE72E"),
            new SettingsNavigationItem(SettingsRoute.About, text("Nav.About"), "\uE946")
        ])
    ];

    private static SettingsNavigationGroup CreateGroup(
        SettingsGroup group,
        string title,
        IReadOnlyList<SettingsNavigationItem> items) =>
        new(group, title, items);
}
