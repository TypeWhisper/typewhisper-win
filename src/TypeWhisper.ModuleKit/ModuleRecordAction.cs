namespace TypeWhisper.ModuleKit;

/// <summary>
/// An action on History records. It is shown in the record details, in the context menu of the record list, and with a
/// file transcription result that History saved.
/// </summary>
/// <param name="Id">A stable identifier; see <see cref="ModuleContributions"/>.</param>
/// <param name="Title">The action's name for one record.</param>
/// <param name="Glyph">A Segoe Fluent Icons code point such as <c>""</c>.</param>
/// <param name="Perform">Runs with the identifiers of the chosen records.</param>
/// <param name="SelectionTitle">The action's name for several records, or null to offer it for one record only.</param>
/// <param name="MaximumRecordCount">
/// The most records the action takes at once, at least 1. With more records chosen it is shown disabled.
/// </param>
/// <param name="SelectionLimitHelp">Why the action is disabled with too many records, or null for the host's text.</param>
public sealed record ModuleRecordAction(
    string Id,
    string Title,
    string Glyph,
    Action<IReadOnlyList<string>> Perform,
    string? SelectionTitle = null,
    int MaximumRecordCount = 1,
    string? SelectionLimitHelp = null);
