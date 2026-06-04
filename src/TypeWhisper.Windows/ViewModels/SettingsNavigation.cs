using CommunityToolkit.Mvvm.ComponentModel;

namespace TypeWhisper.Windows.ViewModels;

/// <summary>
/// Lists the supported settings route values.
/// </summary>
public enum SettingsRoute
{
    /// <summary>
    /// Represents the dashboard option.
    /// </summary>
    Dashboard,
    /// <summary>
    /// Represents the dictation option.
    /// </summary>
    Dictation,
    /// <summary>
    /// Represents the shortcuts option.
    /// </summary>
    Shortcuts,
    /// <summary>
    /// Represents the file transcription option.
    /// </summary>
    FileTranscription,
    /// <summary>
    /// Represents the recorder option.
    /// </summary>
    Recorder,
    /// <summary>
    /// Represents the history option.
    /// </summary>
    History,
    /// <summary>
    /// Represents the dictionary option.
    /// </summary>
    Dictionary,
    /// <summary>
    /// Represents the snippets option.
    /// </summary>
    Snippets,
    /// <summary>
    /// Represents the workflows option.
    /// </summary>
    Workflows,
    /// <summary>
    /// Represents the integrations option.
    /// </summary>
    Integrations,
    /// <summary>
    /// Represents the general option.
    /// </summary>
    General,
    /// <summary>
    /// Represents the appearance option.
    /// </summary>
    Appearance,
    /// <summary>
    /// Represents the advanced option.
    /// </summary>
    Advanced,
    /// <summary>
    /// Represents the premium option.
    /// </summary>
    Premium,
    /// <summary>
    /// Represents the license option.
    /// </summary>
    License,
    /// <summary>
    /// Represents the about option.
    /// </summary>
    About
}

/// <summary>
/// Lists the supported settings group values.
/// </summary>
public enum SettingsGroup
{
    /// <summary>
    /// Represents the overview option.
    /// </summary>
    Overview,
    /// <summary>
    /// Represents the capture option.
    /// </summary>
    Capture,
    /// <summary>
    /// Represents the library option.
    /// </summary>
    Library,
    /// <summary>
    /// Represents the AI option.
    /// </summary>
    AI,
    /// <summary>
    /// Represents the system option.
    /// </summary>
    System
}

/// <summary>
/// Lists the supported settings page kind values.
/// </summary>
public enum SettingsPageKind
{
    /// <summary>
    /// Represents the preference page option.
    /// </summary>
    PreferencePage,
    /// <summary>
    /// Represents the collection page option.
    /// </summary>
    CollectionPage,
    /// <summary>
    /// Represents the guided editor page option.
    /// </summary>
    GuidedEditorPage
}

/// <summary>
/// Represents settings page metadata data.
/// </summary>
/// <param name="Kind">Kind supplied to the member.</param>
/// <param name="ContentWidth">Content width supplied to the member.</param>
/// <param name="ShowsSummaryRow">Shows summary row supplied to the member.</param>
/// <param name="UsesStickyActions">Uses sticky actions supplied to the member.</param>
public sealed record SettingsPageMetadata(
    SettingsPageKind Kind,
    double ContentWidth = 980,
    bool ShowsSummaryRow = true,
    bool UsesStickyActions = false);

/// <summary>
/// Provides settings navigation item behavior.
/// </summary>
public sealed partial class SettingsNavigationItem : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the SettingsNavigationItem class.
    /// </summary>
    public SettingsNavigationItem(SettingsRoute route, string title, string iconGlyph, string? badgeText = null)
    {
        Route = route;
        Title = title;
        IconGlyph = iconGlyph;
        BadgeText = badgeText;
    }

    /// <summary>
    /// Gets the route.
    /// </summary>
    public SettingsRoute Route { get; }
    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }
    /// <summary>
    /// Gets the icon glyph.
    /// </summary>
    public string IconGlyph { get; }
    /// <summary>
    /// Gets the badge text.
    /// </summary>
    public string? BadgeText { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Provides settings navigation group behavior.
/// </summary>
public sealed class SettingsNavigationGroup
{
    /// <summary>
    /// Initializes a new instance of the SettingsNavigationGroup class.
    /// </summary>
    public SettingsNavigationGroup(SettingsGroup group, string title, IReadOnlyList<SettingsNavigationItem> items)
    {
        Group = group;
        Title = title;
        Items = items;
    }

    /// <summary>
    /// Gets the group.
    /// </summary>
    public SettingsGroup Group { get; }
    /// <summary>
    /// Gets the title.
    /// </summary>
    public string Title { get; }
    /// <summary>
    /// Gets the items.
    /// </summary>
    public IReadOnlyList<SettingsNavigationItem> Items { get; }
}

/// <summary>
/// Provides settings navigation catalog behavior.
/// </summary>
public static class SettingsNavigationCatalog
{
    /// <summary>
    /// Performs build.
    /// </summary>
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
