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
