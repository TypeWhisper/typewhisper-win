using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>A device seen in the sync folder, for naming History origins.</summary>
/// <param name="Name">The device name it reported, if any.</param>
/// <param name="Platform">Its platform, such as macOS, iOS or Windows.</param>
public sealed record HistorySyncDevice(string? Name, string Platform);

/// <summary>Per-profile History sync progress, kept apart from dictionary and snippet sync.</summary>
public sealed record HistorySyncState
{
    /// <summary>Whether History and Inbox synchronize.</summary>
    public bool Enabled { get; set; }
    /// <summary>This PC's History origin identity (a lowercase UUID), distinct from the transport device id.</summary>
    public string HistoryDeviceId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>The last published version per component state key, such as <c>history:&lt;uuid&gt;#content</c>.</summary>
    public Dictionary<string, string> ExportedVersions { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Operation ids already read, so they are never applied twice.</summary>
    public HashSet<string> AppliedOperationIds { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Entries the user explicitly deleted here, by History UUID, with the deletion time. Kept 90 days.</summary>
    public Dictionary<string, DateTime> ExplicitDeletions { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Devices found in the sync folder, by History origin identity.</summary>
    public Dictionary<string, HistorySyncDevice> Devices { get; set; } = new(StringComparer.Ordinal);
    /// <summary>When History last synchronized.</summary>
    public DateTime? LastSyncAt { get; set; }
    /// <summary>Whether audio of new entries synchronizes too.</summary>
    public bool AudioEnabled { get; set; }
    /// <summary>When audio sync was turned on; only entries created afterwards exchange audio, as on macOS.</summary>
    public DateTime? AudioSince { get; set; }
}

/// <summary>Access to local History audio for one sync pass.</summary>
/// <param name="LocalPath">Returns the verified local WAV file of a record, or null.</param>
/// <param name="Import">Copies a verified synchronized WAV (path, SHA-256, size) into local History and returns its file name.</param>
/// <param name="Since">Only entries created at or after this time exchange audio.</param>
public sealed record HistorySyncAudioAccess(Func<TranscriptionRecord, string?> LocalPath, Func<string, string, long, string?> Import, DateTime Since);

/// <summary>What one History sync pass did.</summary>
/// <param name="Records">The merged local History, or null when nothing changed locally.</param>
/// <param name="OperationsWritten">Operations published by this PC.</param>
/// <param name="ChangesApplied">Remote changes merged into local History.</param>
public sealed record HistorySyncResult(IReadOnlyList<TranscriptionRecord>? Records, int OperationsWritten, int ChangesApplied)
{
    /// <summary>
    /// The component versions this PC published in this pass. Their operation files exist even when
    /// <see cref="Records"/> is not saved, so keep these versions to avoid publishing the same changes again.
    /// </summary>
    public IReadOnlyDictionary<string, string> Published { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Synchronizes History text and Inbox state through the shared cloud folder, in the operation format of the
/// macOS app: one write-once JSON file per component change, newest change wins per component, and only
/// explicit deletions propagate. Audio of new entries is exchanged as write-once WAV assets when enabled.
/// </summary>
public static class HistoryFolderSync
{
    /// <summary>The only History generation macOS applies.</summary>
    public const string Generation = "history-v1";
    private const int PayloadVersion = 1;
    private static readonly TimeSpan DeletionRetention = TimeSpan.FromDays(90);
    private static readonly Dictionary<string, ((long, DateTime) Stamp, HistorySyncOperation? Operation)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(CloudFolderSyncJson.Options)
    {
        // macOS omits missing optional keys instead of writing null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The History identity of a local record: its id when that is a UUID, otherwise a stable derived UUID.</summary>
    public static Guid SyncId(TranscriptionRecord record) => SyncId(record.Id);

    /// <summary>The History identity for a local record id.</summary>
    public static Guid SyncId(string recordId) =>
        Guid.TryParse(recordId, out var parsed) && parsed != Guid.Empty
            ? parsed : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + recordId)).AsSpan(0, 16));

    /// <summary>The sync item id macOS uses for a History entry.</summary>
    public static string ItemId(Guid id) => "history:" + id.ToString().ToLowerInvariant();

    /// <summary>
    /// Publishes local History changes, reads every History operation in the folder and merges the winners.
    /// Mutates <paramref name="state"/>; persist it only after the returned records were committed.
    /// </summary>
    public static HistorySyncResult Sync(string folderPath, string transportDeviceId, HistorySyncState state,
        IReadOnlyList<TranscriptionRecord> records, string deviceName, string appVersion, DateTime now, CancellationToken cancellationToken = default,
        HistorySyncAudioAccess? audio = null)
    {
        now = Utc(now);
        var package = CloudFolderSyncEngine.PackagePath(folderPath);
        var ownOperations = Path.Combine(package, "ops", Segment(transportDeviceId));
        Directory.CreateDirectory(ownOperations);
        WriteDevice(package, transportDeviceId, state.HistoryDeviceId, deviceName, appVersion, now);
        foreach (var expired in state.ExplicitDeletions.Where(pair => now - pair.Value > DeletionRetention).Select(pair => pair.Key).ToArray())
            state.ExplicitDeletions.Remove(expired);

        var exported = new Dictionary<string, string>(state.ExportedVersions, StringComparer.Ordinal);
        var written = Publish(package, ownOperations, transportDeviceId, state, records, now, audio);
        var published = state.ExportedVersions.Where(pair => !exported.TryGetValue(pair.Key, out var version) || version != pair.Value)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        ReadDevices(package, state);
        var operations = ReadOperations(Path.Combine(package, "ops"), cancellationToken);
        var (merged, applied, deferred) = Merge(package, records, operations, transportDeviceId, state, audio);
        // Audio still downloading from the cloud provider is retried on the next pass.
        state.AppliedOperationIds.UnionWith(operations.Select(operation => operation.OperationId).Where(id => !deferred.Contains(id)));
        state.LastSyncAt = now;
        return new HistorySyncResult(applied > 0 ? merged : null, written, applied) { Published = published };
    }

    // Publishing --------------------------------------------------------------------------------

    private static int Publish(string package, string directory, string deviceId, HistorySyncState state, IReadOnlyList<TranscriptionRecord> records,
        DateTime now, HistorySyncAudioAccess? audio)
    {
        var written = 0;
        var prefix = new DateTimeOffset(now).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        void Write(HistorySyncOperation operation, string stateKey, string version)
        {
            if (state.ExportedVersions.TryGetValue(stateKey, out var exported) && exported == version) return;
            WriteJson(operation, Path.Combine(directory, $"{prefix}-{operation.OperationId}.json"));
            state.ExportedVersions[stateKey] = version;
            written++;
        }
        foreach (var record in records.OrderBy(record => record.Id, StringComparer.Ordinal))
        {
            var id = SyncId(record);
            var item = ItemId(id);
            // Placeholders wait for their content from the originating device.
            if (record.ContentUpdatedAt == DateTime.UnixEpoch) continue;
            var content = Content(record, id, state.HistoryDeviceId);
            Write(Upsert(deviceId, item, "content", content.UpdatedAt) with { HistoryContent = content }, item + "#content", Version(content.UpdatedAt));
            // The originating device owns the Inbox state of its entries until someone here changes it.
            var local = record.OriginDeviceId is null || record.OriginDeviceId == state.HistoryDeviceId;
            if (!local && record.InboxUpdatedAt is null) continue;
            var inbox = Inbox(record, id);
            Write(Upsert(deviceId, item, "inbox", inbox.UpdatedAt) with { HistoryInbox = inbox }, item + "#inbox", Version(inbox.UpdatedAt));
        }
        if (audio is not null)
            foreach (var record in records.Where(record => record.AudioFileName is not null && Utc(record.Timestamp) >= Utc(audio.Since)
                         && (record.OriginDeviceId is null || record.OriginDeviceId == state.HistoryDeviceId)))
            {
                var id = SyncId(record);
                var item = ItemId(id);
                if (state.ExportedVersions.ContainsKey(item + "#audio")) continue;
                if (PublishAsset(package, id, record, audio) is not { } payload) continue;
                Write(Upsert(deviceId, item, "audio", payload.UpdatedAt) with { HistoryAudio = payload }, item + "#audio", payload.Sha256);
            }
        foreach (var (uuid, deletedAt) in state.ExplicitDeletions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var item = "history:" + uuid;
            Write(new HistorySyncOperation
            {
                OperationId = Guid.NewGuid().ToString().ToUpperInvariant(), DeviceId = deviceId, ItemId = item, Kind = "delete",
                UpdatedAt = deletedAt, DeletedAt = deletedAt, HistoryPayloadVersion = PayloadVersion, HistoryGeneration = Generation
            }, item + "#delete", Version(deletedAt));
        }
        return written;
    }

    private static HistorySyncOperation Upsert(string deviceId, string itemId, string component, DateTime updatedAt) => new()
    {
        OperationId = Guid.NewGuid().ToString().ToUpperInvariant(), DeviceId = deviceId, ItemId = itemId, Kind = "upsert",
        UpdatedAt = updatedAt, HistoryPayloadVersion = PayloadVersion, HistoryGeneration = Generation, HistoryComponent = component
    };

    /// <summary>The content payload macOS expects for a local record.</summary>
    public static HistorySyncContent Content(TranscriptionRecord record, Guid id, string historyDeviceId) => new()
    {
        RecordId = id.ToString().ToUpperInvariant(),
        CreatedAt = Utc(record.Timestamp),
        UpdatedAt = Utc(record.ContentUpdatedAt ?? record.Timestamp),
        OriginDeviceId = record.OriginDeviceId ?? historyDeviceId,
        OriginPlatform = record.OriginPlatform ?? "Windows",
        // File transcriptions use the source macOS lists as Imported Files; dictation and recordings are Windows-specific.
        Source = record.OriginSource ?? record.SourceKind switch { "file" => "importedFile", "recording" => "recorder", _ => "windows" },
        ProcessingState = record.ProcessingState ?? (record.Status == TranscriptionRecordStatus.Succeeded ? "ready" : "failed"),
        RawTranscript = record.RawText,
        FinalText = record.FinalText,
        DurationSeconds = double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) : 0,
        EngineDisplayName = record.EngineUsed,
        ModelDisplayName = Blank(record.ModelUsed),
        AppDisplayName = Blank(record.AppName),
        DetectedLanguage = Blank(record.Language),
        ProcessingFailureMessage = record.Status == TranscriptionRecordStatus.Succeeded ? null : Blank(record.WorkflowFailureMessage)
    };

    /// <summary>The Inbox payload macOS expects for a local record; entries never in the Inbox report <c>none</c>.</summary>
    public static HistorySyncInbox Inbox(TranscriptionRecord record, Guid id) => new()
    {
        RecordId = id.ToString().ToUpperInvariant(),
        UpdatedAt = Utc(record.InboxUpdatedAt ?? record.Timestamp),
        State = record.InboxState ?? "none",
        Kind = record.InboxKind,
        CompletionPolicy = record.InboxCompletionPolicy ?? "explicit",
        CompletedAt = record.InboxCompletedAt is { } completed ? Utc(completed) : null,
        SafeAction = record.InboxSafeAction
    };

    // Copies local audio into the package once per content hash, then verifies the copy.
    private static HistorySyncAudio? PublishAsset(string package, Guid id, TranscriptionRecord record, HistorySyncAudioAccess audio)
    {
        try
        {
            if (audio.LocalPath(record) is not { } source) return null;
            var bytes = File.ReadAllBytes(source);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var relative = $"assets/history/{Generation}/{id.ToString().ToLowerInvariant()}/{sha}.wav";
            var destination = AssetPath(package, relative)!;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            // Assets are named by their content hash, so when two devices publish the same recording at once either
            // flushed copy may win; the check below still verifies what ended up in the folder.
            if (!File.Exists(destination))
                AtomicFileWriter.WriteAllBytes(destination, bytes);
            if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))).Equals(sha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The published audio does not match the local recording.");
            var at = Utc(record.Timestamp);
            return new HistorySyncAudio
            {
                RecordId = id.ToString().ToUpperInvariant(), UpdatedAt = at, CreatedAt = at, RelativeAssetPath = relative, MediaType = "audio/wav",
                ByteCount = bytes.LongLength, Sha256 = sha, DurationSeconds = double.IsFinite(record.DurationSeconds) ? Math.Max(0, record.DurationSeconds) : null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The text still synchronizes; audio is retried on the next pass.
            System.Diagnostics.Trace.TraceWarning("History audio was not published: {0}", ex.Message);
            return null;
        }
    }

    // Resolves a package-relative asset path, refusing anything that could leave the package.
    private static string? AssetPath(string package, string relative)
    {
        if (!relative.StartsWith("assets/history/", StringComparison.Ordinal) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(part => part is "" or "." or "..")) return null;
        var root = Path.GetFullPath(package) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    // Reading and merging -------------------------------------------------------------------------

    private static List<HistorySyncOperation> ReadOperations(string operationsPath, CancellationToken cancellationToken)
    {
        var operations = new List<HistorySyncOperation>();
        if (!Directory.Exists(operationsPath)) return operations;
        foreach (var device in Directory.EnumerateDirectories(operationsPath))
            foreach (var file in Directory.EnumerateFiles(device, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(file).StartsWith('.')) continue;
                try
                {
                    // Operation files are write-once, so an unchanged size and time means an unchanged file.
                    var info = new FileInfo(file);
                    var stamp = (info.Length, info.LastWriteTimeUtc);
                    HistorySyncOperation? operation;
                    lock (Cache)
                        if (Cache.TryGetValue(file, out var cached) && cached.Stamp == stamp) { if (cached.Operation is { } hit) operations.Add(hit); continue; }
                    // Dictionary and snippet operations share the folder; only History operations matter here.
                    var text = File.ReadAllText(file);
                    operation = text.Contains("\"history\"", StringComparison.Ordinal) ? JsonSerializer.Deserialize<HistorySyncOperation>(text, Json) : null;
                    if (operation is not null && !IsValid(operation)) operation = null;
                    lock (Cache) Cache[file] = (stamp, operation);
                    if (operation is not null) operations.Add(operation);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
                {
                    // Cloud providers can leave partial or placeholder files; skip them until they are complete.
                }
            }
        return operations;
    }

    /// <summary>Returns whether an operation is a well-formed History operation this version can apply.</summary>
    public static bool IsValid(HistorySyncOperation operation)
    {
        if (operation is not { SchemaVersion: 1, Collection: "history", HistoryGeneration: Generation } || string.IsNullOrWhiteSpace(operation.OperationId)
            || string.IsNullOrWhiteSpace(operation.DeviceId) || operation.UpdatedAt == default || RecordUuid(operation.ItemId) is not { } id) return false;
        return operation.Kind switch
        {
            "delete" => operation.DeletedAt is not null,
            "upsert" => operation.HistoryComponent switch
            {
                "content" => operation.HistoryContent is { } c && Same(c.RecordId, id) && c.RawTranscript is not null && c.FinalText is not null,
                "inbox" => operation.HistoryInbox is { } i && Same(i.RecordId, id) && !string.IsNullOrWhiteSpace(i.State),
                "audio" => operation.HistoryAudio is { } a && Same(a.RecordId, id) && a.IsValid,
                _ => false
            },
            _ => false
        };
    }

    private static (List<TranscriptionRecord> Records, int Applied, HashSet<string> Deferred) Merge(string package, IReadOnlyList<TranscriptionRecord> local,
        IReadOnlyList<HistorySyncOperation> operations, string transportDeviceId, HistorySyncState state, HistorySyncAudioAccess? audio)
    {
        var deferred = new HashSet<string>(StringComparer.Ordinal);
        var records = local.ToList();
        var byId = new Dictionary<Guid, int>();
        for (var i = 0; i < records.Count; i++) byId[SyncId(records[i])] = i;
        var winners = new Dictionary<string, HistorySyncOperation>(StringComparer.Ordinal);
        // This PC's own operations describe what it already has.
        foreach (var operation in operations.Where(operation => operation.DeviceId != transportDeviceId))
        {
            var key = operation.ItemId.ToLowerInvariant() + "#" + (operation.Kind == "delete" ? "delete" : operation.HistoryComponent);
            if (!winners.TryGetValue(key, out var existing) || Prefers(operation, existing)) winners[key] = operation;
        }
        var applied = 0;
        var removed = new HashSet<Guid>();
        foreach (var item in winners.Values.GroupBy(operation => RecordUuid(operation.ItemId)!.Value))
        {
            var id = item.Key;
            var delete = item.FirstOrDefault(operation => operation.Kind == "delete");
            var index = byId.TryGetValue(id, out var found) ? found : -1;
            if (delete is not null)
            {
                if (index >= 0 && Newer(delete.UpdatedAt, delete.DeviceId, Newest(records[index]), transportDeviceId) && !state.AppliedOperationIds.Contains(delete.OperationId))
                {
                    removed.Add(id);
                    applied++;
                    continue;
                }
            }
            foreach (var operation in item.Where(operation => operation.Kind == "upsert").OrderBy(operation => operation.HistoryComponent switch { "content" => 0, "inbox" => 1, _ => 2 }))
            {
                // An older upsert must not resurrect an entry deleted afterwards. An Inbox state that never changed here
                // always yields to the device that set one, even when macOS stamps it with the creation time.
                if (delete is not null && operation.UpdatedAt <= delete.UpdatedAt) continue;
                if (state.AppliedOperationIds.Contains(operation.OperationId)) continue;
                var current = index >= 0 ? records[index] : null;
                if (operation.HistoryComponent == "audio")
                {
                    if (audio is null || current is not { AudioFileName: null }) continue;
                    if (ReceiveAudio(package, operation.HistoryAudio!, audio) is not { } name)
                    {
                        // Not downloaded yet, or not for this PC: keep it for a later pass unless it is too old.
                        if (Utc(operation.HistoryAudio!.CreatedAt) >= Utc(audio.Since).AddMinutes(-5)) deferred.Add(operation.OperationId);
                        continue;
                    }
                    records[index] = current with { AudioFileName = name };
                    state.ExportedVersions[ItemId(id) + "#audio"] = operation.HistoryAudio!.Sha256;
                    applied++;
                    continue;
                }
                TranscriptionRecord? next = operation.HistoryComponent switch
                {
                    "content" when current is null || Newer(operation.UpdatedAt, operation.DeviceId, Utc(current.ContentUpdatedAt ?? current.Timestamp), transportDeviceId)
                        => ApplyContent(current, operation.HistoryContent!, id),
                    "inbox" when current?.InboxUpdatedAt is not { } inboxAt || Newer(operation.UpdatedAt, operation.DeviceId, Utc(inboxAt), transportDeviceId)
                        => ApplyInbox(current ?? Placeholder(id, operation.UpdatedAt), operation.HistoryInbox!),
                    _ => null
                };
                if (next is null) continue;
                if (index >= 0) records[index] = next;
                else { records.Add(next); index = records.Count - 1; byId[id] = index; }
                // What was just received is already current everywhere; do not publish it back.
                var itemId = ItemId(id);
                if (operation.HistoryComponent == "content") state.ExportedVersions[itemId + "#content"] = Version(operation.UpdatedAt);
                else state.ExportedVersions[itemId + "#inbox"] = Version(operation.UpdatedAt);
                applied++;
            }
        }
        if (removed.Count > 0) records.RemoveAll(record => removed.Contains(SyncId(record)));
        return (records.OrderByDescending(record => record.Timestamp).ToList(), applied, deferred);
    }

    // Installs remote audio only once the file is complete and matches its size and hash.
    private static string? ReceiveAudio(string package, HistorySyncAudio payload, HistorySyncAudioAccess audio)
    {
        // As on macOS, audio of entries created before audio sync was turned on stays on its device.
        if (Utc(payload.CreatedAt) < Utc(audio.Since).AddMinutes(-5)) return null;
        if (AssetPath(package, payload.RelativeAssetPath) is not { } path) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != payload.ByteCount || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return audio.Import(path, payload.Sha256, payload.ByteCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Trace.TraceWarning("History audio was not received yet: {0}", ex.Message);
            return null;
        }
    }

    private static DateTime Newest(TranscriptionRecord record) => new[]
    {
        Utc(record.ContentUpdatedAt ?? record.Timestamp), Utc(record.InboxUpdatedAt ?? record.Timestamp)
    }.Max();

    private static bool Newer(DateTime remote, string remoteDevice, DateTime local, string localDevice) =>
        Utc(remote) > local || Utc(remote) == local && string.CompareOrdinal(remoteDevice, localDevice) > 0;

    private static bool Prefers(HistorySyncOperation candidate, HistorySyncOperation existing)
    {
        if (candidate.UpdatedAt != existing.UpdatedAt) return candidate.UpdatedAt > existing.UpdatedAt;
        var device = string.CompareOrdinal(candidate.DeviceId, existing.DeviceId);
        return device != 0 ? device > 0 : string.CompareOrdinal(candidate.OperationId, existing.OperationId) > 0;
    }

    private static TranscriptionRecord Placeholder(Guid id, DateTime at) => new()
    {
        Id = id.ToString(), Timestamp = Utc(at), CreatedAt = Utc(at), RawText = "", FinalText = "", EngineUsed = "remote",
        OriginSource = "other", ProcessingState = "importing", ContentUpdatedAt = DateTime.UnixEpoch
    };

    private static TranscriptionRecord ApplyContent(TranscriptionRecord? current, HistorySyncContent content, Guid id)
    {
        var failed = content.ProcessingState == "failed";
        var baseRecord = current ?? new TranscriptionRecord
        {
            Id = id.ToString(), Timestamp = Utc(content.CreatedAt), CreatedAt = Utc(content.CreatedAt), RawText = "", FinalText = ""
        };
        return baseRecord with
        {
            Timestamp = Utc(content.CreatedAt),
            RawText = content.RawTranscript ?? "",
            // A rendered document is what macOS shows when present.
            FinalText = content.RenderedDocument ?? content.FinalText ?? "",
            DurationSeconds = double.IsFinite(content.DurationSeconds) ? Math.Max(0, content.DurationSeconds) : 0,
            EngineUsed = content.EngineDisplayName ?? "remote",
            ModelUsed = content.ModelDisplayName,
            AppName = content.AppDisplayName,
            Language = content.DetectedLanguage,
            OriginDeviceId = content.OriginDeviceId,
            OriginPlatform = content.OriginPlatform,
            OriginSource = content.Source,
            ProcessingState = content.ProcessingState,
            Status = failed ? TranscriptionRecordStatus.WorkflowPostProcessingFailed : TranscriptionRecordStatus.Succeeded,
            WorkflowFailureMessage = failed ? content.ProcessingFailureMessage ?? "Processing failed on the originating device." : null,
            ContentUpdatedAt = Utc(content.UpdatedAt),
            SourceKind = current?.SourceKind
        };
    }

    private static TranscriptionRecord ApplyInbox(TranscriptionRecord current, HistorySyncInbox inbox) => current with
    {
        InboxState = inbox.State is "none" ? null : inbox.State,
        InboxKind = inbox.Kind,
        InboxCompletionPolicy = inbox.CompletionPolicy,
        InboxCompletedAt = inbox.CompletedAt is { } completed ? Utc(completed) : null,
        InboxSafeAction = inbox.SafeAction,
        InboxUpdatedAt = Utc(inbox.UpdatedAt)
    };

    // Devices -------------------------------------------------------------------------------------

    private static void WriteDevice(string package, string transportId, string historyId, string name, string appVersion, DateTime now) =>
        WriteJson(new HistorySyncDeviceRecord(transportId, historyId, "Windows", appVersion, now, name),
            Path.Combine(package, "devices", Segment(transportId) + ".json"));

    private static void ReadDevices(string package, HistorySyncState state)
    {
        var devices = Path.Combine(package, "devices");
        if (!Directory.Exists(devices)) return;
        foreach (var file in Directory.EnumerateFiles(devices, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<HistorySyncDeviceRecord>(File.ReadAllText(file), Json) is not { } device
                    || string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.Platform)) continue;
                var key = string.IsNullOrWhiteSpace(device.HistoryOriginDeviceId) ? "transport:" + device.DeviceId : device.HistoryOriginDeviceId.Trim();
                state.Devices[key] = new HistorySyncDevice(string.IsNullOrWhiteSpace(device.Name) ? null : device.Name.Trim(), device.Platform);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
    }

    // Helpers -------------------------------------------------------------------------------------

    private static Guid? RecordUuid(string itemId) =>
        itemId.StartsWith("history:", StringComparison.Ordinal) && Guid.TryParse(itemId["history:".Length..], out var id) ? id : null;
    private static bool Same(string? recordId, Guid id) => Guid.TryParse(recordId, out var parsed) && parsed == id;
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static string Version(DateTime value) => Utc(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Any(character => char.IsControl(character) || "\\/<>:\"|?*".Contains(character)))
            throw new ArgumentException("Expected a relative file name segment.", nameof(value));
        return value;
    }

    // Two writers of one device file must not share a temporary name on a shared folder, and the flushed move
    // shows readers a whole file. macOS skips hidden files and reads only .json, so it never sees the temporary file.
    private static void WriteJson<T>(T value, string path) =>
        AtomicFileWriter.WriteAllText(path, JsonSerializer.Serialize(value, Json));
}

/// <summary>A History operation file, compatible with macOS.</summary>
public sealed record HistorySyncOperation
{
    /// <summary>Always 1.</summary>
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    /// <summary>A unique operation id (an uppercase UUID).</summary>
    [JsonPropertyName("operationId")] public string OperationId { get; init; } = "";
    /// <summary>The transport device id of the writer.</summary>
    [JsonPropertyName("deviceId")] public string DeviceId { get; init; } = "";
    /// <summary>Always <c>history</c>.</summary>
    [JsonPropertyName("collection")] public string Collection { get; init; } = "history";
    /// <summary><c>history:&lt;uuid&gt;</c>.</summary>
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    /// <summary><c>upsert</c> or <c>delete</c>.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "upsert";
    /// <summary>The component's change time; for deletes the deletion time.</summary>
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    /// <summary>The deletion time of a delete.</summary>
    [JsonPropertyName("deletedAt")] public DateTime? DeletedAt { get; init; }
    /// <summary>Always 1.</summary>
    [JsonPropertyName("historyPayloadVersion")] public int? HistoryPayloadVersion { get; init; }
    /// <summary>Always <see cref="HistoryFolderSync.Generation"/>.</summary>
    [JsonPropertyName("historyGeneration")] public string? HistoryGeneration { get; init; }
    /// <summary><c>content</c>, <c>inbox</c> or <c>audio</c> for upserts.</summary>
    [JsonPropertyName("historyComponent")] public string? HistoryComponent { get; init; }
    /// <summary>The content payload.</summary>
    [JsonPropertyName("historyContent")] public HistorySyncContent? HistoryContent { get; init; }
    /// <summary>The Inbox payload.</summary>
    [JsonPropertyName("historyInbox")] public HistorySyncInbox? HistoryInbox { get; init; }
    /// <summary>The audio payload.</summary>
    [JsonPropertyName("historyAudio")] public HistorySyncAudio? HistoryAudio { get; init; }
}

/// <summary>History text and metadata, compatible with macOS.</summary>
public sealed record HistorySyncContent
{
    /// <summary>The History UUID.</summary>
    [JsonPropertyName("recordID")] public string RecordId { get; init; } = "";
    /// <summary>When the entry was created.</summary>
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    /// <summary>When the content last changed.</summary>
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    /// <summary>The History origin device identity.</summary>
    [JsonPropertyName("originDeviceID")] public string OriginDeviceId { get; init; } = "";
    /// <summary>The origin platform.</summary>
    [JsonPropertyName("originPlatform")] public string OriginPlatform { get; init; } = "";
    /// <summary>The capture source, such as mac or iPhone.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "other";
    /// <summary>importing, transcribing, ready or failed.</summary>
    [JsonPropertyName("processingState")] public string ProcessingState { get; init; } = "ready";
    /// <summary>The spoken text.</summary>
    [JsonPropertyName("rawTranscript")] public string? RawTranscript { get; init; }
    /// <summary>The final text.</summary>
    [JsonPropertyName("finalText")] public string? FinalText { get; init; }
    /// <summary>A rendered document, when the originating workflow produced one.</summary>
    [JsonPropertyName("renderedDocument")] public string? RenderedDocument { get; init; }
    /// <summary>A structured document from macOS, kept as received.</summary>
    [JsonPropertyName("structuredDocument")] public JsonElement? StructuredDocument { get; init; }
    /// <summary>The recording length.</summary>
    [JsonPropertyName("durationSeconds")] public double DurationSeconds { get; init; }
    /// <summary>The app the text was dictated into.</summary>
    [JsonPropertyName("appDisplayName")] public string? AppDisplayName { get; init; }
    /// <summary>The detected language.</summary>
    [JsonPropertyName("detectedLanguage")] public string? DetectedLanguage { get; init; }
    /// <summary>The transcription engine.</summary>
    [JsonPropertyName("engineDisplayName")] public string? EngineDisplayName { get; init; }
    /// <summary>The transcription model.</summary>
    [JsonPropertyName("modelDisplayName")] public string? ModelDisplayName { get; init; }
    /// <summary>A failure category from the originating device.</summary>
    [JsonPropertyName("processingFailureCategory")] public string? ProcessingFailureCategory { get; init; }
    /// <summary>A failure message from the originating device.</summary>
    [JsonPropertyName("processingFailureMessage")] public string? ProcessingFailureMessage { get; init; }
}

/// <summary>History Inbox state, compatible with macOS.</summary>
public sealed record HistorySyncInbox
{
    /// <summary>The History UUID.</summary>
    [JsonPropertyName("recordID")] public string RecordId { get; init; } = "";
    /// <summary>When the Inbox state last changed.</summary>
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    /// <summary>none, open or completed.</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "none";
    /// <summary>What kind of Inbox item this is, such as watchRecording.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    /// <summary>onOpen, explicit or afterAction.</summary>
    [JsonPropertyName("completionPolicy")] public string CompletionPolicy { get; init; } = "explicit";
    /// <summary>When the item was completed.</summary>
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; init; }
    /// <summary>A suggested action, kept as received.</summary>
    [JsonPropertyName("safeAction")] public JsonElement? SafeAction { get; init; }
}

/// <summary>A History audio asset description, compatible with macOS.</summary>
public sealed record HistorySyncAudio
{
    /// <summary>The History UUID.</summary>
    [JsonPropertyName("recordID")] public string RecordId { get; init; } = "";
    /// <summary>When the audio changed.</summary>
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; init; }
    /// <summary>When the audio was created.</summary>
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    /// <summary>The package-relative path, <c>assets/history/&lt;generation&gt;/&lt;uuid&gt;/&lt;sha256&gt;.wav</c>.</summary>
    [JsonPropertyName("relativeAssetPath")] public string RelativeAssetPath { get; init; } = "";
    /// <summary>Always <c>audio/wav</c>.</summary>
    [JsonPropertyName("mediaType")] public string MediaType { get; init; } = "audio/wav";
    /// <summary>The file size.</summary>
    [JsonPropertyName("byteCount")] public long ByteCount { get; init; }
    /// <summary>The lowercase hex SHA-256 of the file.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";
    /// <summary>The recording length.</summary>
    [JsonPropertyName("durationSeconds")] public double? DurationSeconds { get; init; }

    /// <summary>The same checks macOS applies before trusting a description.</summary>
    [JsonIgnore]
    public bool IsValid => ByteCount >= 0 && RelativeAssetPath.StartsWith("assets/history/", StringComparison.Ordinal)
        && !RelativeAssetPath.Split('/').Any(part => part is "" or "." or "..")
        && Sha256.Length == 64 && Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && (DurationSeconds is null || double.IsFinite(DurationSeconds.Value) && DurationSeconds >= 0);
}

/// <summary>A device file of the sync folder, including the fields macOS uses to name History devices.</summary>
public sealed record HistorySyncDeviceRecord(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("historyOriginDeviceID")] string? HistoryOriginDeviceId,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("name")] string? Name);
