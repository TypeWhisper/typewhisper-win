namespace TypeWhisper.ModuleKit;

/// <summary>Reads History records and their changes.</summary>
public interface IHistoryReading
{
    /// <summary>
    /// The records with these identifiers, in no particular order. Records that are gone, still being processed or
    /// failed are left out.
    /// </summary>
    Task<IReadOnlyList<ModuleHistoryRecord>> RecordsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>The identifiers among <paramref name="ids"/> whose records History still has.</summary>
    Task<IReadOnlySet<string>> ExistingRecordIdsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="handler"/> on the UI thread with <see cref="ModuleHistoryChange.Current"/> once History has
    /// loaded, then after each saved change, until the result is disposed.
    /// </summary>
    IDisposable ObserveChanges(Action<ModuleHistoryChange> handler);

    /// <summary>Opens the record in the History window.</summary>
    void ShowRecord(string id);
}

/// <summary>A saved change to History's records.</summary>
public abstract record ModuleHistoryChange
{
    private ModuleHistoryChange() { }

    /// <summary>
    /// The first change every observer receives: the records History has at that point. Records a module knows that
    /// are not in it were removed before observing started. Later changes are relative to it.
    /// </summary>
    public sealed record Current(IReadOnlySet<string> Ids) : ModuleHistoryChange;

    /// <summary>Records that were added, or whose content changed.</summary>
    public sealed record Upserted(IReadOnlySet<string> Ids) : ModuleHistoryChange;

    /// <summary>Records that were removed.</summary>
    public sealed record Removed(IReadOnlySet<string> Ids) : ModuleHistoryChange;

    /// <summary>Every record was removed.</summary>
    public sealed record Cleared : ModuleHistoryChange;
}

/// <summary>One History record as a module sees it.</summary>
/// <param name="Id">The record's identifier.</param>
/// <param name="RecordedAt">When the record was made.</param>
/// <param name="Source">Where the record came from, such as <c>"dictation"</c> or <c>"file"</c>. Stable values, for filters.</param>
/// <param name="Text">The text as transcribed and corrected.</param>
/// <param name="Language">The language code, or null when unknown.</param>
/// <param name="DurationSeconds">The length of the audio.</param>
/// <param name="AppName">The app a dictation was made in, or null.</param>
/// <param name="AudioFilePath">The full path of the kept audio, or null when History keeps none.</param>
public sealed record ModuleHistoryRecord(
    string Id,
    DateTimeOffset RecordedAt,
    string Source,
    string Text,
    string? Language = null,
    double DurationSeconds = 0,
    string? AppName = null,
    string? AudioFilePath = null);
