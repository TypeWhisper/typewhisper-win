using Microsoft.UI.Xaml;

namespace TypeWhisper.ModuleKit;

/// <summary>
/// A group in the sidebar of the History window, below the host's own sections. The sidebar lists the group's newest
/// entries below its name, all of them on request. While the group is chosen, the detail pane takes the place of the
/// list and shows the view of the chosen entry.
/// </summary>
/// <remarks>
/// The host calls the members on the UI thread, each time it draws the sidebar or the detail pane, so they return
/// quickly. If one throws, the host reports it and leaves out what it would have shown. History's search, filter and
/// sort menus do not apply to the entries.
/// </remarks>
public sealed class ModuleHistorySidebarGroup
{
    /// <summary>Creates a group.</summary>
    /// <param name="id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
    /// <param name="title">The group's name in the sidebar.</param>
    /// <param name="glyph">A Segoe Fluent Icons code point such as <c>""</c>.</param>
    /// <param name="entries">The entries, newest first, in the order the sidebar shows them.</param>
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

    /// <summary>The group's name in the sidebar.</summary>
    public string Title { get; }

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public string Glyph { get; }

    /// <summary>The entries, newest first, in the order the sidebar shows them.</summary>
    public Func<IReadOnlyList<ModuleHistorySidebarEntry>> Entries { get; }

    /// <summary>Creates the view of an entry each time it is chosen.</summary>
    public Func<string, UIElement> CreateView { get; }

    /// <summary>Whether the sidebar shows the group, or null to show it always. Call <see cref="NotifyChanged"/> when the answer changes.</summary>
    public Func<bool>? IsVisible { get; init; }

    /// <summary>
    /// Starts a new entry and returns its identifier, which the host then chooses; null starts none. Offered as a
    /// button next to the group's name, named <see cref="NewTitle"/>, and by choosing the name; or null for none.
    /// </summary>
    public Func<string?>? CreateNew { get; init; }

    /// <summary>The name of the <see cref="CreateNew"/> button, or null for the host's text.</summary>
    public string? NewTitle { get; init; }

    /// <summary>
    /// Deletes an entry after the host asked the user, or null to offer no deletion. Offered in the menu of each entry
    /// in the sidebar and by the Delete key for the chosen entry. Returns false when it could not be deleted; the host then says so.
    /// </summary>
    public Func<string, bool>? Delete { get; init; }

    /// <summary>Raised by <see cref="NotifyChanged"/>.</summary>
    public event Action? Changed;

    /// <summary>
    /// Tells the host that the entries or <see cref="IsVisible"/> changed. Call it on the UI thread. The host draws the
    /// sidebar again. It keeps the chosen entry and its view, unless the entry was listed and no longer
    /// is; a view shows its own changes.
    /// </summary>
    public void NotifyChanged() => Changed?.Invoke();
}

/// <summary>An entry of a <see cref="ModuleHistorySidebarGroup"/>, as the sidebar shows it.</summary>
/// <param name="Id">The entry's identifier within its group.</param>
/// <param name="Title">The text of its row.</param>
/// <param name="Subtitle">More about the entry for its tooltip, or null.</param>
/// <param name="Date">When the entry last changed. The tooltip shows it the way History shows the time of a record.</param>
public sealed record ModuleHistorySidebarEntry(string Id, string Title, string? Subtitle, DateTimeOffset Date);
