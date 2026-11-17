using Microsoft.UI.Xaml;

namespace TypeWhisper.ModuleKit;

/// <summary>
/// A group in the sidebar of the History window, below the host's own sections. While it is chosen, the list shows
/// its entries and the detail pane shows the view of the chosen entry.
/// </summary>
/// <remarks>
/// The host calls the members on the UI thread, each time it draws the sidebar or the list, so they return quickly.
/// If one throws, the host reports it and leaves out what it would have shown. The search field of History filters
/// the entries by title and subtitle; the filter and sort menus do not apply to them.
/// </remarks>
public sealed class ModuleHistorySidebarGroup
{
    /// <summary>Creates a group.</summary>
    /// <param name="id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
    /// <param name="title">The group's name in the sidebar and above the list.</param>
    /// <param name="glyph">A Segoe Fluent Icons code point such as <c>""</c>.</param>
    /// <param name="entries">The entries, in the order the list shows them.</param>
    /// <param name="createView">
    /// Creates the view of an entry each time it is chosen, also for an identifier that <paramref name="entries"/>
    /// does not list yet, such as one that <see cref="CreateNew"/> returned.
    /// </param>
    public ModuleHistorySidebarGroup(string id, string title, string glyph, Func<IReadOnlyList<ModuleHistorySidebarEntry>> entries,
        Func<string, UIElement> createView)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Entries = entries;
        CreateView = createView;
    }

    /// <summary>A stable identifier; see <see cref="ModuleContributions"/>.</summary>
    public string Id { get; }

    /// <summary>The group's name in the sidebar and above the list.</summary>
    public string Title { get; }

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public string Glyph { get; }

    /// <summary>The entries, in the order the list shows them. The sidebar shows their number.</summary>
    public Func<IReadOnlyList<ModuleHistorySidebarEntry>> Entries { get; }

    /// <summary>Creates the view of an entry each time it is chosen.</summary>
    public Func<string, UIElement> CreateView { get; }

    /// <summary>Whether the sidebar shows the group, or null to show it always. Call <see cref="NotifyChanged"/> when the answer changes.</summary>
    public Func<bool>? IsVisible { get; init; }

    /// <summary>
    /// Starts a new entry and returns its identifier, which the host then chooses; null starts none. Offered as a
    /// button above the detail pane, named <see cref="NewTitle"/>, or null for none.
    /// </summary>
    public Func<string?>? CreateNew { get; init; }

    /// <summary>The name of the <see cref="CreateNew"/> button, or null for the host's text.</summary>
    public string? NewTitle { get; init; }

    /// <summary>
    /// Deletes an entry after the host asked the user, or null to offer no deletion. Returns false when it could not be
    /// deleted; the host then says so.
    /// </summary>
    public Func<string, bool>? Delete { get; init; }

    /// <summary>Raised by <see cref="NotifyChanged"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Tells the host that the entries or <see cref="IsVisible"/> changed. Call it on the UI thread. The host draws the
    /// sidebar and the list again. It keeps the chosen entry and its view, unless the entry was listed and no longer
    /// is; a view shows its own changes.
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke();
}

/// <summary>An entry of a <see cref="ModuleHistorySidebarGroup"/>, as the list shows it.</summary>
/// <param name="Id">The entry's identifier within its group.</param>
/// <param name="Title">The first line of its row.</param>
/// <param name="Subtitle">The second line, or null.</param>
/// <param name="Date">When the entry last changed. The row shows it the way History shows the time of a record.</param>
public sealed record ModuleHistorySidebarEntry(string Id, string Title, string? Subtitle, DateTimeOffset Date);
