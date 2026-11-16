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
    string? SelectionLimitHelp = null)
{
    /// <summary>
    /// Whether the action is offered for a record, or null to offer it for every record. With several records chosen,
    /// the action is offered only when this returns true for each of them. It is called on the UI thread each time the
    /// actions are shown, so it returns quickly. If it throws, the action is not offered.
    /// </summary>
    public Func<ModuleRecordInfo, bool>? IsVisible { get; init; }
}

/// <summary>What the host tells <see cref="ModuleRecordAction.IsVisible"/> about a record.</summary>
/// <param name="Id">The record's identifier.</param>
/// <param name="Source">Where the record came from, as <see cref="ModuleHistoryRecord.Source"/> names it.</param>
/// <param name="HasKeptAudio">Whether History keeps the record's audio.</param>
/// <param name="DurationSeconds">The length of the audio.</param>
public sealed record ModuleRecordInfo(string Id, string Source, bool HasKeptAudio, double DurationSeconds);
