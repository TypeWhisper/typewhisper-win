namespace TypeWhisper.WinUIPrototype;

// One in-memory history for the current prototype process. No disk, sync or API.
public sealed class PrototypeHistoryStore(IEnumerable<PrototypeHistoryEntry> initialEntries)
{
    private readonly Dictionary<Guid, PrototypeHistoryEntry> _entries = initialEntries.ToDictionary(entry => entry.RecordId);

    public void Upsert(PrototypeHistoryEntry entry)
    {
        entry.Validate();
        _entries[entry.RecordId] = entry;
    }

    public IReadOnlyList<PrototypeHistoryEntry> Query(string search = "", PrototypeHistoryEntryKind? kind = null, string? deviceId = null)
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

    public IReadOnlyList<PrototypeHistoryOrigin> Devices => _entries.Values
        .Where(entry => !entry.LocalState.SuppressedByLocalRetention)
        .Select(entry => entry.Content.Origin).DistinctBy(origin => origin.DeviceId)
        .OrderByDescending(origin => origin.DeviceId == PrototypeHistoryDevices.ThisPc.DeviceId)
        .ThenBy(origin => origin.DeviceName ?? origin.Platform, StringComparer.OrdinalIgnoreCase).ToArray();
}
