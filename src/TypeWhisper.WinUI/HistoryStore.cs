namespace TypeWhisper.WinUI;

// View snapshot. Persistence is owned by HistoryActions and HistoryReader.
public sealed class HistoryStore(IEnumerable<HistoryEntry> initialEntries)
{
    private readonly Dictionary<Guid, HistoryEntry> _entries = initialEntries.ToDictionary(entry => entry.RecordId);

    public void Remove(Guid recordId) => _entries.Remove(recordId);

    public void Upsert(HistoryEntry entry)
    {
        entry.Validate();
        _entries[entry.RecordId] = entry;
    }

    public IReadOnlyList<HistoryEntry> Query(string search = "", HistoryEntryKind? kind = null, string? deviceId = null)
    {
        search = search.Trim();
        return _entries.Values.Where(entry => !entry.LocalState.SuppressedByLocalRetention
            && (kind is null || entry.Content.Kind == kind)
            && (deviceId is null || entry.Content.Origin.DeviceId == deviceId)
            && (search.Length == 0 || entry.Content.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Content.Transcript?.RawText.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                || entry.Content.Transcript?.FinalText.Contains(search, StringComparison.OrdinalIgnoreCase) == true
                || entry.Content.Transcript?.RenderedDocument?.Contains(search, StringComparison.OrdinalIgnoreCase) == true))
            .OrderByDescending(entry => entry.Content.CreatedAt).ThenBy(entry => entry.RecordId).ToArray();
    }

    public IReadOnlyList<HistoryOrigin> Devices => _entries.Values
        .Where(entry => !entry.LocalState.SuppressedByLocalRetention)
        .Select(entry => entry.Content.Origin).DistinctBy(origin => origin.DeviceId)
        .OrderByDescending(origin => origin.DeviceId == HistoryDevices.ThisPc.DeviceId)
        .ThenBy(origin => origin.DeviceName ?? origin.Platform, StringComparer.OrdinalIgnoreCase).ToArray();
}
