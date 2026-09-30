using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.WinUI;

// History & Inbox sync through the same cloud folder, in the macOS operation format.
internal static partial class WinUICloudSync
{
    private static string HistoryStatePath => WinUIProfile.DataPath("history-sync.json");
    private static HistorySyncState _history = LoadHistoryState();
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

    private static void SaveHistoryState(HistorySyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryStatePath)!);
        var temporary = HistoryStatePath + ".tmp";
        File.WriteAllText(temporary, CloudFolderSyncJson.Serialize(state));
        File.Move(temporary, HistoryStatePath, overwrite: true);
    }

    private static HistorySyncState CloneHistoryState() =>
        CloudFolderSyncJson.Deserialize<HistorySyncState>(CloudFolderSyncJson.Serialize(_history))!;

    internal static void SetHistoryEnabled(bool enabled)
    {
        if (_closing || Busy || enabled == _history.Enabled) return;
        var next = CloneHistoryState();
        next.Enabled = enabled;
        try { SaveHistoryState(next); _history = next; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "History sync could not be changed: " + ex.Message; }
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
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "Audio sync could not be changed: " + ex.Message; }
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
        var snapshot = history.Records.ToArray();
        var state = CloneHistoryState();
        var audio = state.AudioEnabled
            ? new HistorySyncAudioAccess(record => history.ResolveAudioPath(record.AudioFileName),
                (path, sha256, bytes) => history.ImportSyncedAudio(path, sha256, bytes, Lifetime.Token), state.AudioSince ?? DateTime.UtcNow)
            : null;
        var result = await Task.Run(() => HistoryFolderSync.Sync(folder, transport, state, snapshot, Environment.MachineName,
            typeof(WinUICloudSync).Assembly.GetName().Version?.ToString() ?? "unknown", DateTime.UtcNow, Lifetime.Token, audio), Lifetime.Token);
        if (result.Records is { } merged)
        {
            // Local History changed meanwhile: keep it, and merge again on the next pass.
            var current = history.Records;
            if (current.Count != snapshot.Length || current.Where((record, index) => !ReferenceEquals(record, snapshot[index])).Any())
                return "History changed during sync; it will be merged again shortly.";
            // Remote deletions go through the History service so their saved audio is removed as well.
            var kept = merged.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            var removed = snapshot.Where(record => !kept.Contains(record.Id)).Select(record => record.Id).ToArray();
            if (removed.Length > 0 && !history.TryDeleteRecords(removed)) return "Synced History could not be saved. Local History was not changed.";
            if (!history.TryReplaceAll(merged)) return "Synced History could not be saved completely. It will be merged again shortly.";
            // Settle received audio: referenced copies are kept, copies of entries not saved are removed.
            if (audio is not null) history.RetryAudioCleanup();
        }
        var devicesChanged = !state.Devices.OrderBy(pair => pair.Key).SequenceEqual(_history.Devices.OrderBy(pair => pair.Key));
        SaveHistoryState(state);
        _history = state;
        if (devicesChanged) HistoryDevicesChanged?.Invoke();
        return $"History: {result.OperationsWritten} sent · {result.ChangesApplied} applied";
    }
}
