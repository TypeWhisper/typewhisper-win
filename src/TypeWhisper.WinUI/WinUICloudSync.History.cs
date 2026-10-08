using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.WinUI;

// History & Inbox sync through the same cloud folder, in the macOS operation format.
internal static partial class WinUICloudSync
{
    private static string HistoryStatePath => WinUIProfile.DataPath("history-sync.json");
    private static HistorySyncState _history = LoadHistoryState();
    private static TranscriptionRecord[]? _historySnapshot;
    private static long _historySnapshotVersion = -1;
    internal static TypeWhisper.Core.Services.HistoryService? History { get; set; }
    internal static bool HistoryEnabled => _history.Enabled;
    internal static bool HistoryAudioEnabled => _history.AudioEnabled;
    internal static string HistoryDeviceId => _history.HistoryDeviceId;
    internal static IReadOnlyDictionary<string, HistorySyncDevice> HistoryDevices => _history.Devices;
    internal static event Action? HistoryDevicesChanged;

    private static HistorySyncState LoadHistoryState()
    {
        try
        {
            return File.Exists(HistoryStatePath)
                ? CloudFolderSyncJson.Deserialize<HistorySyncState>(File.ReadAllText(HistoryStatePath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unreadable state only costs a full republish; it never deletes History.
            System.Diagnostics.Trace.TraceError("History sync state could not be read: {0}", ex);
            return new();
        }
    }

    private static void SaveHistoryState(HistorySyncState state) =>
        AtomicFileWriter.WriteAllText(HistoryStatePath, CloudFolderSyncJson.Serialize(state));

    private static HistorySyncState CloneHistoryState() =>
        CloudFolderSyncJson.Deserialize<HistorySyncState>(CloudFolderSyncJson.Serialize(_history))!;

    internal static void SetHistoryEnabled(bool enabled)
    {
        if (_closing || Busy || enabled == _history.Enabled) return;
        var next = CloneHistoryState();
        next.Enabled = enabled;
        try { SaveHistoryState(next); _history = next; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("History sync could not be changed: {0}", ex.Message); }
        Changed?.Invoke();
        if (enabled && Preferences.Enabled && CanUse) _ = SyncAsync();
    }

    // As on macOS, audio synchronizes only for entries created after it was turned on.
    internal static void SetHistoryAudioEnabled(bool enabled)
    {
        if (_closing || Busy || enabled == _history.AudioEnabled) return;
        var next = CloneHistoryState();
        next.AudioEnabled = enabled;
        if (enabled) next.AudioSince = DateTime.UtcNow;
        try { SaveHistoryState(next); _history = next; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = Loc.T("Audio sync could not be changed: {0}", ex.Message); }
        Changed?.Invoke();
    }

    // Only deletions the user chose propagate; retention cleanup stays on this PC, as on macOS.
    internal static void RecordHistoryDeletions(IReadOnlyCollection<string> ids, DateTime deletedAt)
    {
        if (!_history.Enabled || ids.Count == 0) return;
        var next = CloneHistoryState();
        foreach (var id in ids) next.ExplicitDeletions[HistoryFolderSync.SyncId(id).ToString()] = deletedAt;
        try { SaveHistoryState(next); _history = next; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceError("History deletion was not journaled: {0}", ex); }
    }

    private static async Task<string?> SyncHistoryAsync()
    {
        if (!_history.Enabled || History is not { } history || Preferences.Folder is not { } folder || Preferences.State?.DeviceId is not { } transport)
            return null;
        await history.EnsureLoadedAsync();
        // Passes run every 15 seconds; copy History only after it changed. Remote changes still need the folder scan.
        var version = history.Version;
        if (_historySnapshot is null || version != _historySnapshotVersion) { _historySnapshot = history.Records.ToArray(); _historySnapshotVersion = version; }
        var snapshot = _historySnapshot;
        var state = CloneHistoryState();
        var audio = state.AudioEnabled
            ? new HistorySyncAudioAccess(record => history.ResolveAudioPath(record.AudioFileName),
                (path, sha256, bytes) => history.ImportSyncedAudio(path, sha256, bytes, Lifetime.Token), state.AudioSince ?? DateTime.UtcNow)
            : null;
        var deletionsBefore = state.ExplicitDeletions.Keys.ToArray();
        var result = await Task.Run(() => HistoryFolderSync.Sync(folder, transport, state, snapshot, Environment.MachineName,
            typeof(WinUICloudSync).Assembly.GetName().Version?.ToString() ?? "unknown", DateTime.UtcNow, Lifetime.Token, audio), Lifetime.Token);
        if (result.Records is { } merged)
        {
            // Local History changed meanwhile: keep it, and merge again on the next pass.
            var current = history.Records;
            if (current.Count != snapshot.Length || current.Where((record, index) => !ReferenceEquals(record, snapshot[index])).Any())
                return KeepPublished(result, Loc.T("History changed during sync; it will be merged again shortly."));
            // Remote deletions go through the History service so their saved audio is removed as well.
            var kept = merged.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            var removed = snapshot.Where(record => !kept.Contains(record.Id)).Select(record => record.Id).ToArray();
            if (removed.Length > 0 && !history.TryDeleteRecords(removed))
                return KeepPublished(result, Loc.T("Synced History could not be saved. Local History was not changed."));
            if (!history.TryReplaceAll(merged))
                return KeepPublished(result, Loc.T("Synced History could not be saved completely. It will be merged again shortly."));
            // Settle received audio: referenced copies are kept, copies of entries not saved are removed.
            if (audio is not null) history.RetryAudioCleanup();
        }
        // Settings and deletions recorded while the pass ran stay; the pass only owns its sync progress.
        var next = CloneHistoryState();
        next.ExportedVersions = state.ExportedVersions;
        next.AppliedOperationIds = state.AppliedOperationIds;
        next.Devices = state.Devices;
        foreach (var expired in deletionsBefore.Where(id => !state.ExplicitDeletions.ContainsKey(id))) next.ExplicitDeletions.Remove(expired);
        // Sync stamps LastSyncAt on every pass; compare before taking it, so a pass that advanced nothing costs no flushed write.
        if (CloudFolderSyncJson.Serialize(next) != CloudFolderSyncJson.Serialize(_history))
        {
            var devicesChanged = !next.Devices.OrderBy(pair => pair.Key).SequenceEqual(_history.Devices.OrderBy(pair => pair.Key));
            next.LastSyncAt = state.LastSyncAt;
            SaveHistoryState(next);
            _history = next;
            if (devicesChanged) HistoryDevicesChanged?.Invoke();
        }
        return Loc.T("History: {0} sent · {1} applied", result.OperationsWritten, result.ChangesApplied);
    }

    // The pass already wrote this PC's operation files. Remember their versions so the next pass does not publish
    // them again. Remote changes stay unapplied and are merged again next time.
    private static string KeepPublished(HistorySyncResult result, string status)
    {
        if (result.Published.Count == 0) return status;
        var next = CloneHistoryState();
        foreach (var (key, version) in result.Published) next.ExportedVersions[key] = version;
        try { SaveHistoryState(next); _history = next; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceError("Published History versions were not saved: {0}", ex); }
        return status;
    }
}
