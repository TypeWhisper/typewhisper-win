namespace TypeWhisper.ModuleKit;

/// <summary>An entry in the tray menu, after History.</summary>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Title">The entry's text.</param>
/// <param name="Perform">Runs after the menu has closed.</param>
/// <param name="Glyph">A Segoe Fluent Icons code point such as <c>""</c>, or null for no icon.</param>
/// <param name="HotkeyActionId">
/// The <see cref="ModuleHotkeyAction.Id"/> whose shortcut the entry shows, or null. An unknown identifier is reported
/// and ignored.
/// </param>
public sealed record ModuleMenuItem(string Id, string Title, Action Perform, string? Glyph = null, string? HotkeyActionId = null);
