using System.Reflection;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

public enum CloudFolderSyncProvider
{
    ICloudDrive,
    OneDrive,
    Dropbox,
    Custom
}

public static class CloudFolderSyncProviderDetector
{
    public static CloudFolderSyncProvider Detect(string? folderPath)
    {
        var path = (folderPath ?? string.Empty).ToLowerInvariant();
        if (path.Contains("mobile documents", StringComparison.Ordinal) ||
            path.Contains("icloud drive", StringComparison.Ordinal) ||
            path.Contains("iclouddrive", StringComparison.Ordinal))
        {
            return CloudFolderSyncProvider.ICloudDrive;
        }

        if (path.Contains("onedrive", StringComparison.Ordinal))
            return CloudFolderSyncProvider.OneDrive;

        if (path.Contains("dropbox", StringComparison.Ordinal))
            return CloudFolderSyncProvider.Dropbox;

        return CloudFolderSyncProvider.Custom;
    }
}

public sealed class CloudFolderSyncNotEntitledException : InvalidOperationException
{
    public CloudFolderSyncNotEntitledException()
        : base("Cloud Folder Sync requires an active Commercial license.")
    {
    }
}

public sealed class CloudFolderSyncMissingStoreException : InvalidOperationException
{
    public CloudFolderSyncMissingStoreException()
        : base("TypeWhisper user data is unavailable.")
    {
    }
}

public sealed record CloudFolderSyncState
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public HashSet<string> KnownLocalItemIds { get; set; } = [];
    public Dictionary<string, string> ExportedItemVersions { get; set; } = [];
    public HashSet<string> AppliedOperationIds { get; set; } = [];
    public DateTime? LastSyncAt { get; set; }
}

public sealed record CloudFolderSyncResult(
    int OperationsRead,
    int OperationsWritten,
    int MutationsApplied,
    DateTime SyncedAt);

public sealed record CloudFolderSyncManifest(int SchemaVersion, string CreatedBy, DateTime UpdatedAt);

public sealed record CloudFolderSyncDeviceRecord(
    string DeviceId,
    string Platform,
    string AppVersion,
    DateTime UpdatedAt);

public enum CloudFolderSyncOperationKind
{
    Upsert,
    Delete
}

public sealed record CloudFolderSyncOperation
{
    public int SchemaVersion { get; init; } = 1;
    public string OperationId { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public UserDataSyncCollection Collection { get; init; }
    public string ItemId { get; init; } = string.Empty;
    public CloudFolderSyncOperationKind Kind { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? DeletedAt { get; init; }
    public UserDataSyncDictionaryEntry? Dictionary { get; init; }
    public UserDataSyncSnippet? Snippet { get; init; }

    public static CloudFolderSyncOperation UpsertDictionary(
        UserDataSyncDictionaryEntry entry,
        string itemId,
        string deviceId,
        string? operationId = null) =>
        new()
        {
            SchemaVersion = 1,
            OperationId = operationId ?? Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            Collection = UserDataSyncCollection.Dictionary,
            ItemId = itemId,
            Kind = CloudFolderSyncOperationKind.Upsert,
            UpdatedAt = entry.UpdatedAt,
            Dictionary = entry
        };

    public static CloudFolderSyncOperation UpsertSnippet(
        UserDataSyncSnippet snippet,
        string itemId,
        string deviceId,
        string? operationId = null) =>
        new()
        {
            SchemaVersion = 1,
            OperationId = operationId ?? Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            Collection = UserDataSyncCollection.Snippets,
            ItemId = itemId,
            Kind = CloudFolderSyncOperationKind.Upsert,
            UpdatedAt = snippet.UpdatedAt,
            Snippet = snippet
        };

    public static CloudFolderSyncOperation Delete(
        UserDataSyncCollection collection,
        string itemId,
        string deviceId,
        DateTime deletedAt,
        string? operationId = null) =>
        new()
        {
            SchemaVersion = 1,
            OperationId = operationId ?? Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            Collection = collection,
            ItemId = itemId,
            Kind = CloudFolderSyncOperationKind.Delete,
            UpdatedAt = deletedAt,
            DeletedAt = deletedAt
        };
}

public sealed record CloudFolderSyncRecord(
    UserDataSyncCollection Collection,
    string ItemId,
    DateTime UpdatedAt,
    string Version,
    UserDataSyncDictionaryEntry? Dictionary,
    UserDataSyncSnippet? Snippet);

public static class CloudFolderSyncEngine
{
    private const string PackageDirectoryName = "typewhisper-sync";
    private const string ManifestFileName = "manifest.json";
    private const string DevicesDirectoryName = "devices";
    private const string OperationsDirectoryName = "ops";
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);

    public static string PackagePath(string folderPath) =>
        Path.Combine(folderPath, EnsureRelativePathSegment(PackageDirectoryName, nameof(PackageDirectoryName)));

    public static Task<CloudFolderSyncResult> SyncAsync(
        string folderPath,
        IUserDataSyncStore? store,
        CloudFolderSyncState state,
        PaidEntitlements entitlements,
        DateTime? now = null)
    {
        if (!entitlements.CanUseCloudFolderSync)
            throw new CloudFolderSyncNotEntitledException();

        if (store is null)
            throw new CloudFolderSyncMissingStoreException();

        var syncNow = NormalizeUtc(now ?? DateTime.UtcNow);
        var deviceId = EnsureRelativePathSegment(state.DeviceId, nameof(state.DeviceId));
        var packagePath = PackagePath(folderPath);
        var operationsPath = Path.Combine(
            packagePath,
            EnsureRelativePathSegment(OperationsDirectoryName, nameof(OperationsDirectoryName)));
        var deviceOperationsPath = Path.Combine(operationsPath, deviceId);
        var devicesPath = Path.Combine(
            packagePath,
            EnsureRelativePathSegment(DevicesDirectoryName, nameof(DevicesDirectoryName)));

        Directory.CreateDirectory(deviceOperationsPath);
        Directory.CreateDirectory(devicesPath);
        WritePackageMetadata(packagePath, devicesPath, deviceId, syncNow);

        var initialRecords = RecordsFrom(store.Snapshot());
        var localOperations = MakeLocalOperations(initialRecords, state, syncNow);
        Write(localOperations, deviceOperationsPath, syncNow);
        PruneExpiredTombstones(deviceOperationsPath, syncNow);

        var operations = ReadOperations(operationsPath);
        var winners = WinningOperations(operations);
        var mutations = MakeMutations(winners, initialRecords, state.DeviceId, state.AppliedOperationIds);

        if (mutations.Count > 0)
            store.Apply(mutations);

        var finalRecords = RecordsFrom(store.Snapshot());
        state.KnownLocalItemIds = finalRecords.Keys.ToHashSet(StringComparer.Ordinal);
        state.ExportedItemVersions = finalRecords.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Version,
            StringComparer.Ordinal);
        state.AppliedOperationIds.UnionWith(operations.Select(operation => operation.OperationId));
        state.LastSyncAt = syncNow;

        return Task.FromResult(new CloudFolderSyncResult(
            operations.Count,
            localOperations.Count,
            mutations.Count,
            syncNow));
    }

    public static Dictionary<string, CloudFolderSyncRecord> RecordsFrom(UserDataSyncSnapshot snapshot)
    {
        var records = new Dictionary<string, CloudFolderSyncRecord>(StringComparer.Ordinal);

        foreach (var entry in snapshot.DictionaryEntries)
        {
            var itemId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
            Merge(new CloudFolderSyncRecord(
                UserDataSyncCollection.Dictionary,
                itemId,
                NormalizeUtc(entry.UpdatedAt),
                VersionString(entry.UpdatedAt),
                entry,
                null), records);
        }

        foreach (var snippet in snapshot.Snippets)
        {
            var itemId = UserDataSyncIdentity.SnippetItemId(snippet.Trigger);
            Merge(new CloudFolderSyncRecord(
                UserDataSyncCollection.Snippets,
                itemId,
                NormalizeUtc(snippet.UpdatedAt),
                VersionString(snippet.UpdatedAt),
                null,
                snippet), records);
        }

        return records;
    }

    public static Dictionary<string, CloudFolderSyncOperation> WinningOperations(IEnumerable<CloudFolderSyncOperation> operations)
    {
        var winners = new Dictionary<string, CloudFolderSyncOperation>(StringComparer.Ordinal);

        foreach (var operation in operations.Where(IsValidOperation))
        {
            if (!winners.TryGetValue(operation.ItemId, out var existing) ||
                Prefers(operation, existing))
            {
                winners[operation.ItemId] = operation;
            }
        }

        return winners;
    }

    private static bool IsValidOperation(CloudFolderSyncOperation operation) =>
        operation.SchemaVersion == 1 &&
        (operation.Kind == CloudFolderSyncOperationKind.Delete ||
         operation.Dictionary is not null ||
         operation.Snippet is not null);

    private static void Merge(
        CloudFolderSyncRecord candidate,
        Dictionary<string, CloudFolderSyncRecord> records)
    {
        if (!records.TryGetValue(candidate.ItemId, out var existing) ||
            Prefers(candidate, existing))
        {
            records[candidate.ItemId] = candidate;
        }
    }

    private static bool Prefers(CloudFolderSyncRecord candidate, CloudFolderSyncRecord existing)
    {
        if (candidate.UpdatedAt != existing.UpdatedAt)
            return candidate.UpdatedAt > existing.UpdatedAt;

        return RecordTieBreaker(candidate).CompareTo(RecordTieBreaker(existing)) > 0;
    }

    private static string RecordTieBreaker(CloudFolderSyncRecord record)
    {
        if (record.Dictionary is { } dictionary)
        {
            return string.Join("|",
                record.Collection,
                dictionary.EntryType,
                dictionary.Original,
                dictionary.Replacement ?? string.Empty,
                dictionary.CaseSensitive,
                dictionary.IsEnabled,
                VersionString(dictionary.CreatedAt));
        }

        if (record.Snippet is { } snippet)
        {
            return string.Join("|",
                record.Collection,
                snippet.Trigger,
                snippet.Replacement,
                snippet.CaseSensitive,
                snippet.IsEnabled,
                string.Join(",", snippet.Tags),
                VersionString(snippet.CreatedAt));
        }

        return record.ItemId;
    }

    private static IReadOnlyList<CloudFolderSyncOperation> MakeLocalOperations(
        IReadOnlyDictionary<string, CloudFolderSyncRecord> records,
        CloudFolderSyncState state,
        DateTime now)
    {
        var operations = new List<CloudFolderSyncOperation>();

        foreach (var record in records.Values.OrderBy(record => record.ItemId, StringComparer.Ordinal))
        {
            if (state.ExportedItemVersions.TryGetValue(record.ItemId, out var exportedVersion) &&
                exportedVersion == record.Version)
            {
                continue;
            }

            if (record.Dictionary is { } dictionary)
                operations.Add(CloudFolderSyncOperation.UpsertDictionary(dictionary, record.ItemId, state.DeviceId));
            else if (record.Snippet is { } snippet)
                operations.Add(CloudFolderSyncOperation.UpsertSnippet(snippet, record.ItemId, state.DeviceId));
        }

        foreach (var itemId in state.KnownLocalItemIds.Except(records.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var collection = itemId.StartsWith("snippet:", StringComparison.Ordinal)
                ? UserDataSyncCollection.Snippets
                : UserDataSyncCollection.Dictionary;
            operations.Add(CloudFolderSyncOperation.Delete(collection, itemId, state.DeviceId, now));
        }

        return operations;
    }

    private static IReadOnlyList<UserDataSyncMutation> MakeMutations(
        IReadOnlyDictionary<string, CloudFolderSyncOperation> winners,
        IReadOnlyDictionary<string, CloudFolderSyncRecord> localRecords,
        string localDeviceId,
        IReadOnlySet<string> appliedOperationIds)
    {
        var mutations = new List<UserDataSyncMutation>();

        foreach (var operation in winners.Values
                     .OrderBy(operation => operation.ItemId, StringComparer.Ordinal)
                     .Where(operation => !appliedOperationIds.Contains(operation.OperationId))
                     .Where(operation =>
                     {
                         localRecords.TryGetValue(operation.ItemId, out var local);
                         return ShouldApply(operation, local, localDeviceId);
                     }))
        {
            switch (operation.Kind, operation.Collection)
            {
                case (CloudFolderSyncOperationKind.Delete, UserDataSyncCollection.Dictionary):
                    mutations.Add(new UserDataSyncMutation.DeleteDictionary(operation.ItemId));
                    break;
                case (CloudFolderSyncOperationKind.Delete, UserDataSyncCollection.Snippets):
                    mutations.Add(new UserDataSyncMutation.DeleteSnippet(operation.ItemId));
                    break;
                case (CloudFolderSyncOperationKind.Upsert, UserDataSyncCollection.Dictionary):
                    if (operation.Dictionary is { } dictionary)
                        mutations.Add(new UserDataSyncMutation.UpsertDictionary(dictionary));
                    break;
                case (CloudFolderSyncOperationKind.Upsert, UserDataSyncCollection.Snippets):
                    if (operation.Snippet is { } snippet)
                        mutations.Add(new UserDataSyncMutation.UpsertSnippet(snippet));
                    break;
            }
        }

        return mutations;
    }

    private static bool ShouldApply(
        CloudFolderSyncOperation operation,
        CloudFolderSyncRecord? local,
        string localDeviceId)
    {
        if (local is null)
            return operation.Kind == CloudFolderSyncOperationKind.Upsert;

        if (operation.UpdatedAt > local.UpdatedAt)
            return true;

        return operation.UpdatedAt == local.UpdatedAt &&
               operation.DeviceId.CompareTo(localDeviceId) > 0;
    }

    private static bool Prefers(CloudFolderSyncOperation candidate, CloudFolderSyncOperation existing)
    {
        if (candidate.UpdatedAt != existing.UpdatedAt)
            return candidate.UpdatedAt > existing.UpdatedAt;

        if (!string.Equals(candidate.DeviceId, existing.DeviceId, StringComparison.Ordinal))
            return candidate.DeviceId.CompareTo(existing.DeviceId) > 0;

        return candidate.OperationId.CompareTo(existing.OperationId) > 0;
    }

    private static void WritePackageMetadata(string packagePath, string devicesPath, string deviceId, DateTime now)
    {
        WriteJson(
            new CloudFolderSyncManifest(1, "TypeWhisper", now),
            Path.Combine(packagePath, EnsureRelativePathSegment(ManifestFileName, nameof(ManifestFileName))));

        WriteJson(
            new CloudFolderSyncDeviceRecord(deviceId, "Windows", CurrentAppVersion(), now),
            Path.Combine(devicesPath, $"{EnsureRelativePathSegment(deviceId, nameof(deviceId))}.json"));
    }

    private static void Write(IReadOnlyList<CloudFolderSyncOperation> operations, string directory, DateTime now)
    {
        foreach (var operation in operations)
        {
            var fileName = $"{OperationTimestamp(now)}-{operation.OperationId}.json";
            WriteJson(operation, Path.Combine(directory, fileName));
        }
    }

    private static IReadOnlyList<CloudFolderSyncOperation> ReadOperations(string operationsPath)
    {
        if (!Directory.Exists(operationsPath))
            return [];

        var operations = new List<CloudFolderSyncOperation>();

        foreach (var deviceDirectory in Directory.EnumerateDirectories(operationsPath))
        {
            foreach (var file in Directory.EnumerateFiles(deviceDirectory, "*.json"))
            {
                try
                {
                    var operation = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(
                        File.ReadAllText(file));
                    if (operation is not null)
                        operations.Add(operation);
                }
                catch
                {
                    // Cloud providers can leave transient or partial files; skip unreadable operations.
                }
            }
        }

        return operations;
    }

    private static void PruneExpiredTombstones(string deviceOperationsPath, DateTime now)
    {
        if (!Directory.Exists(deviceOperationsPath))
            return;

        foreach (var file in Directory.EnumerateFiles(deviceOperationsPath, "*.json"))
        {
            try
            {
                var operation = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(
                    File.ReadAllText(file));
                if (operation?.Kind == CloudFolderSyncOperationKind.Delete &&
                    operation.DeletedAt is { } deletedAt &&
                    now - NormalizeUtc(deletedAt) > TombstoneRetention)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort pruning only.
            }
        }
    }

    private static void WriteJson<T>(T value, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{path}.tmp";
        File.WriteAllText(tempPath, CloudFolderSyncJson.Serialize(value));
        File.Move(tempPath, path, overwrite: true);
    }

    private static string OperationTimestamp(DateTime date) =>
        new DateTimeOffset(NormalizeUtc(date)).ToUnixTimeMilliseconds().ToString();

    private static string VersionString(DateTime date)
    {
        var utc = NormalizeUtc(date);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DateTime NormalizeUtc(DateTime date) =>
        date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime();

    private static string EnsureRelativePathSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment) ||
            Path.IsPathRooted(segment) ||
            segment.Contains(Path.DirectorySeparatorChar) ||
            segment.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Expected a relative file name segment.", parameterName);
        }

        return segment;
    }

    private static string CurrentAppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
