using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>
/// Lists the supported cloud folder sync provider values.
/// </summary>
public enum CloudFolderSyncProvider
{
    /// <summary>
    /// Represents the i cloud drive option.
    /// </summary>
    ICloudDrive,
    /// <summary>
    /// Represents the one drive option.
    /// </summary>
    OneDrive,
    /// <summary>
    /// Represents the dropbox option.
    /// </summary>
    Dropbox,
    /// <summary>
    /// Represents the custom option.
    /// </summary>
    Custom
}

/// <summary>
/// Provides cloud folder sync provider detector behavior.
/// </summary>
public static class CloudFolderSyncProviderDetector
{
    /// <summary>
    /// Detects.
    /// </summary>
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

/// <summary>
/// Provides cloud folder sync not entitled exception behavior.
/// </summary>
public sealed class CloudFolderSyncNotEntitledException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the CloudFolderSyncNotEntitledException class.
    /// </summary>
    public CloudFolderSyncNotEntitledException()
        : base(Loc.T("Cloud Folder Sync requires an active Commercial license."))
    {
    }
}

/// <summary>
/// Provides cloud folder sync missing store exception behavior.
/// </summary>
public sealed class CloudFolderSyncMissingStoreException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the CloudFolderSyncMissingStoreException class.
    /// </summary>
    public CloudFolderSyncMissingStoreException()
        : base(Loc.T("TypeWhisper user data is unavailable."))
    {
    }
}

/// <summary>
/// Represents cloud folder sync state data.
/// </summary>
public sealed record CloudFolderSyncState
{
    /// <summary>
    /// Returns the device id.
    /// </summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    /// <summary>
    /// Gets or sets the known local item ids value.
    /// </summary>
    public HashSet<string> KnownLocalItemIds { get; set; } = [];
    /// <summary>
    /// Gets or sets the exported item versions value.
    /// </summary>
    public Dictionary<string, string> ExportedItemVersions { get; set; } = [];
    /// <summary>
    /// Gets or sets the applied operation ids value.
    /// </summary>
    public HashSet<string> AppliedOperationIds { get; set; } = [];
    /// <summary>
    /// Gets or sets the last sync at value.
    /// </summary>
    public DateTime? LastSyncAt { get; set; }
}

/// <summary>
/// Represents cloud folder sync result data.
/// </summary>
/// <param name="OperationsRead">Operations read supplied to the member.</param>
/// <param name="OperationsWritten">Operations written supplied to the member.</param>
/// <param name="MutationsApplied">Mutations applied supplied to the member.</param>
/// <param name="SyncedAt">Synced at supplied to the member.</param>
/// <param name="HeldForReview">
/// Received entries that were saved turned off because they use a regular expression, line breaks or the
/// clipboard. They stay off until someone turns them on in this app.
/// </param>
public sealed record CloudFolderSyncResult(
    int OperationsRead,
    int OperationsWritten,
    int MutationsApplied,
    DateTime SyncedAt,
    int HeldForReview = 0);

/// <summary>
/// Represents cloud folder sync manifest data.
/// </summary>
/// <param name="SchemaVersion">Schema version supplied to the member.</param>
/// <param name="CreatedBy">Created by supplied to the member.</param>
/// <param name="UpdatedAt">Updated at supplied to the member.</param>
public sealed record CloudFolderSyncManifest(int SchemaVersion, string CreatedBy, DateTime UpdatedAt);

/// <summary>
/// Represents cloud folder sync device record data.
/// </summary>
/// <param name="DeviceId">Device id supplied to the member.</param>
/// <param name="Platform">Platform supplied to the member.</param>
/// <param name="AppVersion">App version supplied to the member.</param>
/// <param name="UpdatedAt">Updated at supplied to the member.</param>
/// <param name="Name">The name macOS shows for this device.</param>
public sealed record CloudFolderSyncDeviceRecord(
    string DeviceId,
    string Platform,
    string AppVersion,
    DateTime UpdatedAt,
    string? Name = null);

/// <summary>
/// Lists the supported cloud folder sync operation kind values.
/// </summary>
public enum CloudFolderSyncOperationKind
{
    /// <summary>
    /// Represents the upsert option.
    /// </summary>
    Upsert,
    /// <summary>
    /// Represents the delete option.
    /// </summary>
    Delete
}

/// <summary>
/// Represents cloud folder sync operation data.
/// </summary>
public sealed record CloudFolderSyncOperation
{
    /// <summary>
    /// Gets or sets the schema version value.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;
    /// <summary>
    /// Gets or sets the operation id value.
    /// </summary>
    public string OperationId { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the device id value.
    /// </summary>
    public string DeviceId { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the collection value.
    /// </summary>
    public UserDataSyncCollection Collection { get; init; }
    /// <summary>
    /// Gets or sets the item id value.
    /// </summary>
    public string ItemId { get; init; } = string.Empty;
    /// <summary>
    /// Gets or sets the kind value.
    /// </summary>
    public CloudFolderSyncOperationKind Kind { get; init; }
    /// <summary>
    /// Gets or sets the updated at value.
    /// </summary>
    public DateTime UpdatedAt { get; init; }
    /// <summary>
    /// Gets or sets the deleted at value.
    /// </summary>
    public DateTime? DeletedAt { get; init; }
    /// <summary>
    /// Gets or sets the dictionary value.
    /// </summary>
    public UserDataSyncDictionaryEntry? Dictionary { get; init; }
    /// <summary>
    /// Gets or sets the snippet value.
    /// </summary>
    public UserDataSyncSnippet? Snippet { get; init; }

    /// <summary>
    /// Upserts dictionary.
    /// </summary>
    public static CloudFolderSyncOperation UpsertDictionary(
        UserDataSyncDictionaryEntry entry,
        string itemId,
        string deviceId,
        string? operationId = null) =>
        new()
        {
            SchemaVersion = entry.EntryType == UserDataSyncDictionaryEntryType.Correction && entry.IsRegex ? 2 : 1,
            OperationId = operationId ?? Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            Collection = UserDataSyncCollection.Dictionary,
            ItemId = itemId,
            Kind = CloudFolderSyncOperationKind.Upsert,
            UpdatedAt = entry.UpdatedAt,
            Dictionary = entry
        };

    /// <summary>
    /// Upserts snippet.
    /// </summary>
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

    /// <summary>
    /// Performs delete.
    /// </summary>
    public static CloudFolderSyncOperation Delete(
        UserDataSyncCollection collection,
        string itemId,
        string deviceId,
        DateTime deletedAt,
        string? operationId = null) =>
        new()
        {
            SchemaVersion = itemId.StartsWith(UserDataSyncIdentity.RegexCorrectionPrefix, StringComparison.Ordinal) ? 2 : 1,
            OperationId = operationId ?? Guid.NewGuid().ToString(),
            DeviceId = deviceId,
            Collection = collection,
            ItemId = itemId,
            Kind = CloudFolderSyncOperationKind.Delete,
            UpdatedAt = deletedAt,
            DeletedAt = deletedAt
        };
}

/// <summary>
/// Represents cloud folder sync record data.
/// </summary>
/// <param name="Collection">Collection supplied to the member.</param>
/// <param name="ItemId">Item id supplied to the member.</param>
/// <param name="UpdatedAt">Updated at supplied to the member.</param>
/// <param name="Version">Version supplied to the member.</param>
/// <param name="Dictionary">Dictionary supplied to the member.</param>
/// <param name="Snippet">Snippet supplied to the member.</param>
public sealed record CloudFolderSyncRecord(
    UserDataSyncCollection Collection,
    string ItemId,
    DateTime UpdatedAt,
    string Version,
    UserDataSyncDictionaryEntry? Dictionary,
    UserDataSyncSnippet? Snippet);

/// <summary>
/// Provides cloud folder sync engine behavior.
/// </summary>
public static class CloudFolderSyncEngine
{
    private const string PackageDirectoryName = "typewhisper-sync";
    private const string ManifestFileName = "manifest.json";
    private const string DevicesDirectoryName = "devices";
    private const string OperationsDirectoryName = "ops";

    /// <summary>The most entries a local dictionary or snippet catalog holds; the app refuses larger catalogs.</summary>
    public const int MaxCatalogItems = 10_000;
    private const int MaxKeyLength = 160;
    private const int MaxReplacementLength = 10_000;
    private const int MaxTagsLength = 300;

    /// <summary>
    /// Gets the package path.
    /// </summary>
    public static string PackagePath(string folderPath) =>
        Path.Combine(folderPath, EnsureRelativePathSegment(PackageDirectoryName, nameof(PackageDirectoryName)));

    /// <summary>
    /// Returns the folder to sync with when the user picked an existing sync package itself. Both apps add the
    /// package folder, so syncing the package directly would create a nested package the Mac never reads.
    /// </summary>
    public static string SyncFolder(string chosenFolder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(chosenFolder);
        return string.Equals(Path.GetFileName(trimmed), PackageDirectoryName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(trimmed, ManifestFileName)) && Path.GetDirectoryName(trimmed) is { Length: > 0 } parent
                ? parent : chosenFolder;
    }

    /// <summary>
    /// Performs sync asynchronously.
    /// </summary>
    /// <param name="folderPath">The chosen sync folder.</param>
    /// <param name="store">The local catalogs.</param>
    /// <param name="state">This device's sync progress; updated by the pass.</param>
    /// <param name="entitlements">Whether sync may run.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the pass before local changes are applied.</param>
    /// <param name="reservedDictionaryItems">
    /// Dictionary entries outside the store snapshot, such as installed term packs, that count toward
    /// <see cref="MaxCatalogItems"/>.
    /// </param>
    public static Task<CloudFolderSyncResult> SyncAsync(
        string folderPath,
        IUserDataSyncStore? store,
        CloudFolderSyncState state,
        PaidEntitlements entitlements,
        DateTime? now = null, CancellationToken cancellationToken = default,
        int reservedDictionaryItems = 0)
    {
        if (!entitlements.CanUseCloudFolderSync)
            throw new CloudFolderSyncNotEntitledException();

        if (store is null)
            throw new CloudFolderSyncMissingStoreException();

        cancellationToken.ThrowIfCancellationRequested();
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

        foreach (var directory in new[] { packagePath, operationsPath, deviceOperationsPath, devicesPath })
            CloudFolderSyncFiles.EnsureNotLinked(directory);
        Directory.CreateDirectory(deviceOperationsPath);
        Directory.CreateDirectory(devicesPath);
        WritePackageMetadata(packagePath, devicesPath, deviceId, syncNow);

        var initialSnapshot = store.Snapshot();
        var initialRecords = RecordsFrom(initialSnapshot);
        var legacyRegexIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var publicationState = state;
        if (state.KnownLocalItemIds.Any(id => id.StartsWith("dictionary:correction:", StringComparison.Ordinal) &&
            (!state.ExportedItemVersions.TryGetValue(id, out var version) || !version.StartsWith("dictionary-v3|", StringComparison.Ordinal))))
        {
            // Use the version this device actually synced. Unseen remote edits cannot
            // change which exact regex a local deletion or mode change targets.
            var previousOperations = ReadOperations(operationsPath, cancellationToken, out var unreadableHistory);
            var applied = previousOperations.Where(operation => state.AppliedOperationIds.Contains(operation.OperationId)).ToArray();
            var known = LegacySynchronizedCorrections(applied, state);
            if (!LegacyCorrectionHistoryIsComplete(state, known))
            {
                if (applied.Length > 0 || unreadableHistory)
                    throw new IOException(Loc.T("Previously synced correction files are missing or unreadable. Wait for your cloud provider to download them, or choose a new sync folder if the old files were deleted."));
                // Republish local data while retaining unambiguous local deletions.
                // Only unchanged regex rules can withdraw their old folded IDs.
                // Changed rules may have replaced literals that still need deleting.
                var migratedIds = initialRecords.Values
                    .Where(record => record.Dictionary is { EntryType: UserDataSyncDictionaryEntryType.Correction, IsRegex: true })
                    .Select(record => record.Dictionary!)
                    .Where(entry => state.ExportedItemVersions.TryGetValue(UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original), out var version) &&
                        (version == VersionString(entry.UpdatedAt) || version == "dictionary-v2|" + VersionString(entry.UpdatedAt)))
                    .Select(entry => UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original));
                publicationState = state with
                {
                    KnownLocalItemIds = state.KnownLocalItemIds.Except(migratedIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal),
                    ExportedItemVersions = []
                };
            }
            foreach (var (id, operation) in known)
                if (state.KnownLocalItemIds.Contains(id) &&
                    (!state.ExportedItemVersions.TryGetValue(id, out var version) || !version.StartsWith("dictionary-v3|", StringComparison.Ordinal)) &&
                    operation.Kind == CloudFolderSyncOperationKind.Upsert &&
                    operation.Dictionary is { EntryType: UserDataSyncDictionaryEntryType.Correction, IsRegex: true } entry)
                    legacyRegexIds[id] = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, true);
        }
        var localOperations = MakeLocalOperations(initialRecords, publicationState, legacyRegexIds, syncNow);
        Write(localOperations, deviceOperationsPath, syncNow);
        // Keep tombstones: deleting them while old upserts remain would resurrect data on a new device.
        cancellationToken.ThrowIfCancellationRequested();

        // Changes dated implausibly far ahead would win every later conflict; they wait until their time comes.
        var readable = ReadOperations(operationsPath, cancellationToken, out _).Where(operation => IsPlausible(operation, syncNow)).ToArray();
        var operations = NormalizeIdentities(readable, initialRecords, out var observedOperationIds);
        var winners = WinningOperations(operations);
        var localCounts = new Dictionary<UserDataSyncCollection, int>
        {
            [UserDataSyncCollection.Dictionary] = initialSnapshot.DictionaryEntries.Count + Math.Max(0, reservedDictionaryItems),
            [UserDataSyncCollection.Snippets] = initialSnapshot.Snippets.Count
        };
        var plan = MakeMutations(winners, initialRecords, state.DeviceId, state.AppliedOperationIds, localCounts);
        var mutations = plan.Mutations;

        cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count > 0)
            store.Apply(mutations);

        var finalRecords = RecordsFrom(store.Snapshot());
        state.KnownLocalItemIds = finalRecords.Keys.ToHashSet(StringComparer.Ordinal);
        state.ExportedItemVersions = finalRecords.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Version,
            StringComparer.Ordinal);
        // Publish preserved mode metadata and migrated v1 regex entries once.
        foreach (var mutation in mutations.OfType<UserDataSyncMutation.UpsertDictionary>())
        {
            var entry = mutation.Entry;
            var itemId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex);
            // An entry turned off for review here must not publish its off state back to its other devices.
            if (plan.HeldItemIds.Contains(itemId)) continue;
            if (entry.EntryType == UserDataSyncDictionaryEntryType.Correction &&
                (!entry.IsRegexFieldPresent || operations.Any(operation =>
                    operation.SchemaVersion == 1 && !state.AppliedOperationIds.Contains(operation.OperationId) &&
                    operation.Dictionary?.IsRegex == true && operation.ItemId == itemId)))
                state.ExportedItemVersions.Remove(itemId);
        }
        // Suppressed v1 regex upserts were seen too. Remember them even if a
        // winning tombstone later disappears, so the stale rule cannot reappear.
        // Additions skipped because a catalog is full are read again once there is room.
        state.AppliedOperationIds.UnionWith(observedOperationIds.Where(id => !plan.DeferredOperationIds.Contains(id)));
        state.LastSyncAt = syncNow;

        return Task.FromResult(new CloudFolderSyncResult(
            operations.Count,
            localOperations.Count,
            mutations.Count,
            syncNow,
            plan.HeldItemIds.Count));
    }

    /// <summary>
    /// Performs records from.
    /// </summary>
    public static Dictionary<string, CloudFolderSyncRecord> RecordsFrom(UserDataSyncSnapshot snapshot)
    {
        var records = new Dictionary<string, CloudFolderSyncRecord>(StringComparer.Ordinal);

        foreach (var entry in snapshot.DictionaryEntries)
        {
            var itemId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex);
            Merge(new CloudFolderSyncRecord(
                UserDataSyncCollection.Dictionary,
                itemId,
                NormalizeUtc(entry.UpdatedAt),
                "dictionary-v3|" + VersionString(entry.UpdatedAt),
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

    /// <summary>
    /// Returns the winning operations.
    /// </summary>
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

    // Received entries must fit the limits of the local catalogs, or the Dictionary and Snippets pages refuse to
    // open them. Entries that do not fit are left out on this device.
    private static bool IsValidOperation(CloudFolderSyncOperation operation) =>
        IsSupportedSchema(operation) && !string.IsNullOrWhiteSpace(operation.OperationId) &&
        !string.IsNullOrWhiteSpace(operation.DeviceId) && !string.IsNullOrWhiteSpace(operation.ItemId) &&
        operation.UpdatedAt != default && Enum.IsDefined(operation.Collection) && Enum.IsDefined(operation.Kind) &&
        (operation.Kind == CloudFolderSyncOperationKind.Delete ||
         operation.Collection == UserDataSyncCollection.Dictionary && operation.Dictionary is { } d &&
            IsValidDictionaryOriginal(d) && Enum.IsDefined(d.EntryType) && Enum.IsDefined(d.Source) &&
            (d.EntryType != UserDataSyncDictionaryEntryType.Correction || d.Replacement is { Length: <= MaxReplacementLength }) &&
            (d.CtcMinSimilarity is null || float.IsFinite(d.CtcMinSimilarity.Value) && d.CtcMinSimilarity >= .4f && d.CtcMinSimilarity <= .95f) ||
         operation.Collection == UserDataSyncCollection.Snippets && operation.Snippet is { } n && IsValidSnippet(n));

    private static bool IsPlausible(CloudFolderSyncOperation operation, DateTime now) =>
        CloudFolderSyncFiles.IsPlausible(operation.UpdatedAt, now) && CloudFolderSyncFiles.IsPlausible(operation.DeletedAt, now) &&
        CloudFolderSyncFiles.IsPlausible(operation.Dictionary?.UpdatedAt, now) && CloudFolderSyncFiles.IsPlausible(operation.Snippet?.UpdatedAt, now);

    // Keys are saved trimmed, as the Dictionary and Snippets pages save them.
    private static bool IsValidKey(string? key) =>
        key?.Trim() is { Length: > 0 and <= MaxKeyLength } trimmed && trimmed.IndexOfAny(['\r', '\n']) < 0;

    private static bool IsValidSnippet(UserDataSyncSnippet snippet)
    {
        if (!IsValidKey(snippet.Trigger) || snippet.Replacement is not { Length: <= MaxReplacementLength } ||
            snippet.Tags is null || snippet.Tags.Any(tag => tag is null) || string.Join(",", snippet.Tags).Length > MaxTagsLength)
            return false;
        try
        {
            // A date or time placeholder with an invalid format would make every expansion of this snippet fail.
            var probe = new Snippet { Id = "sync", Trigger = snippet.Trigger.Trim(), Replacement = snippet.Replacement };
            _ = SnippetService.ApplySnippetsSnapshot(probe.Trigger, [probe], () => "");
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool IsValidDictionaryOriginal(UserDataSyncDictionaryEntry entry)
    {
        if (entry.EntryType != UserDataSyncDictionaryEntryType.Correction || !entry.IsRegex)
            return IsValidKey(entry.Original);
        // Match the persisted catalog's single-line key limits before saving.
        if (string.IsNullOrEmpty(entry.Original) || entry.Original.Length > MaxKeyLength ||
            entry.Original.Contains('\r') || entry.Original.Contains('\n')) return false;
        try
        {
            _ = new Regex(entry.Original, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsSupportedSchema(CloudFolderSyncOperation operation) =>
        operation.SchemaVersion == 1 || operation.SchemaVersion == 2 &&
        operation.Collection == UserDataSyncCollection.Dictionary &&
        operation.ItemId?.StartsWith(UserDataSyncIdentity.RegexCorrectionPrefix, StringComparison.Ordinal) == true &&
        (operation.Kind == CloudFolderSyncOperationKind.Delete ||
         operation.Dictionary is { EntryType: UserDataSyncDictionaryEntryType.Correction, IsRegex: true } entry &&
         !string.IsNullOrEmpty(entry.Original) &&
         operation.ItemId == UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, true));

    private static IReadOnlyList<CloudFolderSyncOperation> NormalizeIdentities(
        IReadOnlyList<CloudFolderSyncOperation> operations, IReadOnlyDictionary<string, CloudFolderSyncRecord> local,
        out IReadOnlyCollection<string> observedOperationIds)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        void Remember(CloudFolderSyncRecord record)
        {
            var legacy = record.Dictionary is { } d ? $"dictionary:{UserDataSyncIdentity.JsonName(d.EntryType)}:{UserDataSyncIdentity.NormalizedKey(d.Original)}"
                : record.Snippet is { } n ? "snippet:" + UserDataSyncIdentity.NormalizedKey(n.Trigger) : record.ItemId;
            // Folded legacy tombstones cannot identify which regex was deleted.
            aliases[legacy] = record.Dictionary is { } entry
                ? UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original)
                : record.ItemId;
        }
        foreach (var record in local.Values) Remember(record);
        CloudFolderSyncOperation PreserveLegacyRegexMode(CloudFolderSyncOperation operation)
        {
            // Resolve omitted mode before validation: a space-only pattern is valid
            // only when it addresses an existing regex, not a new literal correction.
            if (operation.SchemaVersion == 1 && operation.Kind == CloudFolderSyncOperationKind.Upsert &&
                operation.Collection == UserDataSyncCollection.Dictionary &&
                operation.Dictionary is { EntryType: UserDataSyncDictionaryEntryType.Correction, IsRegexFieldPresent: false } legacy &&
                !string.IsNullOrEmpty(legacy.Original) &&
                !local.ContainsKey(UserDataSyncIdentity.DictionaryItemId(legacy.EntryType, legacy.Original)) && local.Values.Any(record =>
                    record.Dictionary is { EntryType: UserDataSyncDictionaryEntryType.Correction, IsRegex: true } known &&
                    string.Equals(known.Original, legacy.Original, StringComparison.Ordinal)))
                return operation with { Dictionary = legacy with { IsRegex = true } };
            return operation;
        }
        var valid = operations.Select(PreserveLegacyRegexMode).Where(IsValidOperation).ToArray();
        observedOperationIds = valid.Select(operation => operation.OperationId).ToArray();
        // Superseded upserts still identify the target of an unencoded tombstone.
        // Keep every v1 alias in the folded namespace, including legacy regexes.
        foreach (var operation in valid.Where(operation => operation.SchemaVersion == 1 && operation.Kind == CloudFolderSyncOperationKind.Upsert))
        {
            var id = operation.Dictionary is { } entry
                ? UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original)
                : UserDataSyncIdentity.SnippetItemId(operation.Snippet!.Trigger);
            aliases[operation.ItemId] = id;
            Remember(new(operation.Collection, id, operation.UpdatedAt, "", operation.Dictionary, operation.Snippet));
        }
        var legacyWinners = LegacyWinningOperations(valid);
        var canonical = valid.Where(operation => operation.SchemaVersion != 1 || operation.Kind != CloudFolderSyncOperationKind.Upsert ||
            operation.Dictionary is not { EntryType: UserDataSyncDictionaryEntryType.Correction } entry ||
            legacyWinners[UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original)].OperationId == operation.OperationId)
            .Select(operation =>
        {
            if (operation.Kind != CloudFolderSyncOperationKind.Upsert) return operation;
            var id = operation.Collection == UserDataSyncCollection.Dictionary
                ? UserDataSyncIdentity.DictionaryItemId(operation.Dictionary!.EntryType, operation.Dictionary.Original, operation.Dictionary.IsRegex)
                : UserDataSyncIdentity.SnippetItemId(operation.Snippet!.Trigger);
            return operation with { ItemId = id };
        }).ToArray();
        return canonical.Select(operation => operation.SchemaVersion == 1 && operation.Kind == CloudFolderSyncOperationKind.Delete &&
            (operation.ItemId.StartsWith(UserDataSyncIdentity.RegexCorrectionPrefix, StringComparison.Ordinal) || !local.ContainsKey(operation.ItemId))
                ? operation with { ItemId = LegacyItemId(operation.ItemId, aliases) } : operation).ToArray();
    }

    private static string LegacyItemId(string itemId, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.TryGetValue(itemId, out var canonical)) return canonical;
        // A v1 Base64URL suffix cannot contain a colon. In schema 1 this tag is
        // part of a raw literal, never an exact-pattern v2 identity.
        return itemId.StartsWith(UserDataSyncIdentity.RegexCorrectionPrefix, StringComparison.Ordinal)
            ? UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Correction, itemId["dictionary:correction:".Length..])
            : itemId;
    }

    private static IReadOnlyDictionary<string, CloudFolderSyncOperation> LegacySynchronizedCorrections(
        IReadOnlyList<CloudFolderSyncOperation> operations, CloudFolderSyncState state)
    {
        // The exported version records the local synchronized state even if a peer's
        // clock gave an older operation or tombstone a later timestamp.
        // Already-applied history is evidence of the old state, not a new import.
        // Current catalog limits must not make a readable old version disappear.
        var known = new Dictionary<string, CloudFolderSyncOperation>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (operation.SchemaVersion != 1 || operation.Collection != UserDataSyncCollection.Dictionary ||
                operation.Kind != CloudFolderSyncOperationKind.Upsert || operation.UpdatedAt == default ||
                string.IsNullOrWhiteSpace(operation.OperationId) || string.IsNullOrWhiteSpace(operation.DeviceId) ||
                string.IsNullOrWhiteSpace(operation.ItemId) || !state.AppliedOperationIds.Contains(operation.OperationId) ||
                operation.Dictionary is not { EntryType: UserDataSyncDictionaryEntryType.Correction } entry ||
                string.IsNullOrEmpty(entry.Original)) continue;
            var id = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
            if (!state.KnownLocalItemIds.Contains(id) || !state.ExportedItemVersions.TryGetValue(id, out var version) ||
                version != VersionString(entry.UpdatedAt) && version != "dictionary-v2|" + VersionString(entry.UpdatedAt)) continue;
            if (!known.TryGetValue(id, out var existing) || Prefers(operation, existing)) known[id] = operation;
        }
        return known;
    }

    private static bool LegacyCorrectionHistoryIsComplete(
        CloudFolderSyncState state, IReadOnlyDictionary<string, CloudFolderSyncOperation> known)
    {
        foreach (var itemId in state.KnownLocalItemIds)
        {
            if (!itemId.StartsWith("dictionary:correction:", StringComparison.Ordinal) ||
                itemId.StartsWith(UserDataSyncIdentity.RegexCorrectionPrefix, StringComparison.Ordinal) ||
                !state.ExportedItemVersions.TryGetValue(itemId, out var version) ||
                version.StartsWith("dictionary-v3|", StringComparison.Ordinal)) continue;
            // Only the last exported correction version is required. Applied IDs of
            // unrelated tombstones can legitimately outlive their pruned files.
            if (!known.TryGetValue(itemId, out var operation) || operation.Kind != CloudFolderSyncOperationKind.Upsert ||
                operation.Dictionary is not { EntryType: UserDataSyncDictionaryEntryType.Correction } entry) return false;
            var timestamp = VersionString(entry.UpdatedAt);
            if (version != timestamp && version != "dictionary-v2|" + timestamp) return false;
        }
        return true;
    }

    // Resolve v1 identities before migrating. Deleted or renamed legacy patterns
    // must not reappear as new regex entries on an upgraded device.
    private static IReadOnlyDictionary<string, CloudFolderSyncOperation> LegacyWinningOperations(
        IReadOnlyList<CloudFolderSyncOperation> operations)
    {
        var valid = operations.Where(operation => operation.SchemaVersion == 1 && IsValidOperation(operation)).ToArray();
        var legacyAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in valid.Where(operation => operation.Dictionary is not null))
        {
            var entry = operation.Dictionary!;
            var key = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
            legacyAliases[operation.ItemId] = key;
            legacyAliases[$"dictionary:{UserDataSyncIdentity.JsonName(entry.EntryType)}:{UserDataSyncIdentity.NormalizedKey(entry.Original)}"] = key;
        }
        return WinningOperations(valid
            .Select(operation => operation.Dictionary is { } entry
                ? operation with { ItemId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) }
                : operation with { ItemId = LegacyItemId(operation.ItemId, legacyAliases) }));
    }

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
                dictionary.IsRegexFieldPresent,
                dictionary.IsRegex,
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
        IReadOnlyDictionary<string, string> legacyRegexIds,
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

        // A regex deleted before its first upgraded sync has no exact ID in state yet.
        // Publish its v2 tombstone even when the old ID is now occupied by a literal.
        foreach (var (_, regexId) in legacyRegexIds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!records.ContainsKey(regexId) && !state.KnownLocalItemIds.Contains(regexId))
                operations.Add(CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, regexId, state.DeviceId, now));

        foreach (var itemId in state.KnownLocalItemIds.Except(records.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // Only the same, previously synced regex still present under its exact ID is
            // a migration. A literal-to-regex edit must delete the old literal normally.
            if (legacyRegexIds.TryGetValue(itemId, out var regexId) && records.ContainsKey(regexId))
                continue;
            var collection = itemId.StartsWith("snippet:", StringComparison.Ordinal)
                ? UserDataSyncCollection.Snippets
                : UserDataSyncCollection.Dictionary;
            operations.Add(CloudFolderSyncOperation.Delete(collection, itemId, state.DeviceId, now));
        }

        return operations;
    }

    private sealed record MutationPlan(
        List<UserDataSyncMutation> Mutations,
        HashSet<string> HeldItemIds,
        HashSet<string> DeferredOperationIds);

    private static MutationPlan MakeMutations(
        IReadOnlyDictionary<string, CloudFolderSyncOperation> winners,
        IReadOnlyDictionary<string, CloudFolderSyncRecord> localRecords,
        string localDeviceId,
        IReadOnlySet<string> appliedOperationIds,
        IReadOnlyDictionary<UserDataSyncCollection, int> localCounts)
    {
        var plan = new MutationPlan([], new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        var pending = winners.Values
            .OrderBy(operation => operation.ItemId, StringComparer.Ordinal)
            .Where(operation => !appliedOperationIds.Contains(operation.OperationId))
            .Select(operation => (Operation: operation, Local: localRecords.GetValueOrDefault(operation.ItemId)))
            .Where(pair => ShouldApply(pair.Operation, pair.Local, localDeviceId))
            .ToArray();
        // Deletions make room before additions are counted against the catalog limit.
        var counts = new Dictionary<UserDataSyncCollection, int>(localCounts);
        foreach (var (operation, local) in pending)
            if (operation.Kind == CloudFolderSyncOperationKind.Delete && local is not null)
                counts[operation.Collection] = counts.GetValueOrDefault(operation.Collection) - 1;
        bool HasRoom(CloudFolderSyncOperation operation, CloudFolderSyncRecord? local)
        {
            if (local is not null) return true;
            if (counts.GetValueOrDefault(operation.Collection) >= MaxCatalogItems)
            {
                plan.DeferredOperationIds.Add(operation.OperationId);
                return false;
            }
            counts[operation.Collection] = counts.GetValueOrDefault(operation.Collection) + 1;
            return true;
        }

        foreach (var (operation, local) in pending)
        {
            switch (operation.Kind, operation.Collection)
            {
                case (CloudFolderSyncOperationKind.Delete, UserDataSyncCollection.Dictionary):
                    plan.Mutations.Add(new UserDataSyncMutation.DeleteDictionary(operation.ItemId));
                    break;
                case (CloudFolderSyncOperationKind.Delete, UserDataSyncCollection.Snippets):
                    plan.Mutations.Add(new UserDataSyncMutation.DeleteSnippet(operation.ItemId));
                    break;
                case (CloudFolderSyncOperationKind.Upsert, UserDataSyncCollection.Dictionary):
                    if (operation.Dictionary is { } dictionary && HasRoom(operation, local))
                    {
                        var entry = ForLocalCatalog(dictionary);
                        if (NeedsReview(entry, local?.Dictionary))
                        {
                            entry = entry with { IsEnabled = false };
                            plan.HeldItemIds.Add(operation.ItemId);
                        }
                        plan.Mutations.Add(new UserDataSyncMutation.UpsertDictionary(entry));
                    }
                    break;
                case (CloudFolderSyncOperationKind.Upsert, UserDataSyncCollection.Snippets):
                    if (operation.Snippet is { } snippet && HasRoom(operation, local))
                    {
                        var received = snippet with { Trigger = snippet.Trigger.Trim() };
                        if (NeedsReview(received, local?.Snippet))
                        {
                            received = received with { IsEnabled = false };
                            plan.HeldItemIds.Add(operation.ItemId);
                        }
                        plan.Mutations.Add(new UserDataSyncMutation.UpsertSnippet(received));
                    }
                    break;
            }
        }

        return plan;
    }

    // Terms carry no replacement, and literal phrases are saved trimmed, as the Dictionary page saves them.
    private static UserDataSyncDictionaryEntry ForLocalCatalog(UserDataSyncDictionaryEntry entry) => entry with
    {
        Original = entry.EntryType == UserDataSyncDictionaryEntryType.Correction && entry.IsRegex ? entry.Original : entry.Original.Trim(),
        Replacement = entry.EntryType == UserDataSyncDictionaryEntryType.Term ? null : entry.Replacement
    };

    // Anyone who can write to the sync folder can add entries. Entries that can rewrite whole dictations, insert
    // line breaks (which can submit text in chat apps and terminals) or insert the clipboard are saved turned off,
    // unless this device already has the same entry turned on. Turning such an entry on here accepts it.
    private static bool NeedsReview(UserDataSyncDictionaryEntry entry, UserDataSyncDictionaryEntry? local) =>
        entry.IsEnabled && entry.EntryType == UserDataSyncDictionaryEntryType.Correction &&
        (entry.IsRegex || HasLineBreak(entry.Replacement, includeEscapes: true)) &&
        !(local is { IsEnabled: true } && local.EntryType == entry.EntryType && local.IsRegex == entry.IsRegex &&
          local.CaseSensitive == entry.CaseSensitive && string.Equals(local.Original, entry.Original, StringComparison.Ordinal) &&
          string.Equals(local.Replacement, entry.Replacement, StringComparison.Ordinal));

    private static bool NeedsReview(UserDataSyncSnippet snippet, UserDataSyncSnippet? local) =>
        snippet.IsEnabled &&
        (snippet.Replacement.Contains("{clipboard", StringComparison.Ordinal) || HasLineBreak(snippet.Replacement, includeEscapes: false)) &&
        !(local is { IsEnabled: true } && local.CaseSensitive == snippet.CaseSensitive &&
          string.Equals(local.Trigger, snippet.Trigger, StringComparison.Ordinal) &&
          string.Equals(local.Replacement, snippet.Replacement, StringComparison.Ordinal));

    // Dictionary replacements expand \n and \r escapes into line breaks; snippets insert their text as written.
    private static bool HasLineBreak(string? text, bool includeEscapes) =>
        text is not null && (text.IndexOfAny(['\r', '\n', '\u0085', (char)0x2028, (char)0x2029]) >= 0 ||
            includeEscapes && (text.Contains(@"\n", StringComparison.Ordinal) || text.Contains(@"\r", StringComparison.Ordinal)));

    private static bool ShouldApply(
        CloudFolderSyncOperation operation,
        CloudFolderSyncRecord? local,
        string localDeviceId)
    {
        if (local is null)
            return operation.Kind == CloudFolderSyncOperationKind.Upsert;

        if (operation.UpdatedAt > local.UpdatedAt)
            return true;

        if (operation.UpdatedAt != local.UpdatedAt) return false;
        if (RegexPresencePreference(operation.Dictionary, local.Dictionary) is { } presence) return presence;
        return operation.DeviceId.CompareTo(localDeviceId) > 0;
    }

    private static bool Prefers(CloudFolderSyncOperation candidate, CloudFolderSyncOperation existing)
    {
        if (candidate.UpdatedAt != existing.UpdatedAt)
            return candidate.UpdatedAt > existing.UpdatedAt;

        if (RegexPresencePreference(candidate.Dictionary, existing.Dictionary) is { } presence) return presence;

        if (!string.Equals(candidate.DeviceId, existing.DeviceId, StringComparison.Ordinal))
            return candidate.DeviceId.CompareTo(existing.DeviceId) > 0;

        return candidate.OperationId.CompareTo(existing.OperationId) > 0;
    }

    private static bool? RegexPresencePreference(UserDataSyncDictionaryEntry? candidate, UserDataSyncDictionaryEntry? existing) =>
        candidate?.EntryType == UserDataSyncDictionaryEntryType.Correction &&
        existing?.EntryType == UserDataSyncDictionaryEntryType.Correction &&
        candidate.IsRegexFieldPresent != existing.IsRegexFieldPresent
            ? candidate.IsRegexFieldPresent : null;

    private static void WritePackageMetadata(string packagePath, string devicesPath, string deviceId, DateTime now)
    {
        var manifestPath = Path.Combine(packagePath, EnsureRelativePathSegment(ManifestFileName, nameof(ManifestFileName)));
        // The manifest identifies the package format. Device records carry sync liveness on both platforms.
        if (!ValidateManifestIfPresent(manifestPath))
        {
            try
            {
                AtomicFileWriter.CreateAllText(manifestPath, CloudFolderSyncJson.Serialize(new CloudFolderSyncManifest(1, "TypeWhisper", now)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!ValidateManifestIfPresent(manifestPath)) throw;
                // Another device published the complete manifest first. Keep its timestamp and unknown fields.
                // Never replace incompatible/unreadable metadata or contend on this shared file on every pass.
            }
        }

        var devicePath = Path.Combine(devicesPath, $"{EnsureRelativePathSegment(deviceId, nameof(deviceId))}.json");
        var existing = ReadDevice(devicePath);
        // macOS lists every device file, so an unnamed one appears as "Device"; History sync may have named it already.
        var name = existing?["name"] is JsonValue saved && saved.TryGetValue<string>(out var savedName) && savedName.Length > 0 ? savedName : Environment.MachineName;
        var device = JsonSerializer.SerializeToNode(new CloudFolderSyncDeviceRecord(deviceId, "Windows", CurrentAppVersion(), now, name), CloudFolderSyncJson.Options)!.AsObject();
        // Keep what History sync adds, such as the History id macOS uses to group this device's entries.
        if (existing is not null)
            foreach (var (key, value) in existing)
                if (!device.ContainsKey(key)) device[key] = value?.DeepClone();
        WriteJson(device, devicePath);
    }

    private static bool ValidateManifestIfPresent(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var manifest = JsonSerializer.Deserialize<CloudFolderSyncManifest>(stream, CloudFolderSyncJson.Options);
            if (manifest is not { SchemaVersion: 1, CreatedBy: "TypeWhisper" })
                throw new CloudFolderSyncManifestException(path, unsupportedFormat: true);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (CloudFolderSyncManifestException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CloudFolderSyncManifestException(path, unsupportedFormat: false, ex);
        }
    }

    private static JsonObject? ReadDevice(string path)
    {
        try { return File.Exists(path) && CloudFolderSyncFiles.ReadSmallFile(path) is { } text ? JsonNode.Parse(text) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Write(IReadOnlyList<CloudFolderSyncOperation> operations, string directory, DateTime now)
    {
        foreach (var operation in operations)
        {
            var fileName = $"{OperationTimestamp(now)}-{operation.OperationId}.json";
            WriteJson(operation, Path.Combine(directory, fileName));
        }
    }

    private static IReadOnlyList<CloudFolderSyncOperation> ReadOperations(
        string operationsPath, CancellationToken cancellationToken, out bool unreadableHistory)
    {
        unreadableHistory = false;
        if (!Directory.Exists(operationsPath))
            return [];

        var operations = new List<CloudFolderSyncOperation>();

        foreach (var deviceDirectory in CloudFolderSyncFiles.DeviceDirectories(operationsPath))
        {
            foreach (var file in CloudFolderSyncFiles.OperationFiles(deviceDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Linked or oversized files are never read and count as unreadable.
                    if (CloudFolderSyncFiles.ReadSmallFile(file) is not { } text)
                    {
                        unreadableHistory = true;
                        continue;
                    }
                    using var document = JsonDocument.Parse(text);
                    var envelope = document.RootElement;
                    if (!HasReadableOperationEnvelope(envelope))
                    {
                        unreadableHistory = true;
                        continue;
                    }
                    // Valid Mac history and future schemas are not partial files.
                    if (envelope.TryGetProperty("collection", out var collection) && collection.ValueKind == JsonValueKind.String && collection.GetString() == "history" ||
                        envelope.TryGetProperty("schemaVersion", out var schema) && schema.TryGetInt32(out var version) && version > 2) continue;
                    var operation = envelope.Deserialize<CloudFolderSyncOperation>(CloudFolderSyncJson.Options);
                    if (operation is not null && HasReadableOperationStructure(operation))
                    {
                        // Keep readable old payloads as migration evidence, regardless
                        // of current import limits. Defaults from partial objects are
                        // not evidence that the old package was deliberately removed.
                        operations.Add(operation);
                    }
                    else unreadableHistory = true;
                }
                catch
                {
                    // A completely unreadable old package is not proof it was reset.
                    unreadableHistory = true;
                }
            }
        }

        return operations;
    }

    private static bool HasReadableOperationEnvelope(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) || version < 1) return false;
        foreach (var field in new[] { "operationId", "deviceId", "itemId" })
            if (!envelope.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString())) return false;
        // Future schemas may add collection/kind values. Preserve support for
        // both string and integer enum encodings without interpreting their payloads.
        foreach (var field in new[] { "collection", "kind" })
        {
            if (!envelope.TryGetProperty(field, out var value)) return false;
            if (value.ValueKind == JsonValueKind.String)
            {
                if (string.IsNullOrWhiteSpace(value.GetString())) return false;
            }
            else if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) return false;
        }
        if (version <= 2 && !Enum.IsDefined(envelope.GetProperty("kind").Deserialize<CloudFolderSyncOperationKind>(CloudFolderSyncJson.Options)))
            return false;
        return envelope.TryGetProperty("updatedAt", out var updatedAt) && updatedAt.ValueKind == JsonValueKind.String &&
            updatedAt.Deserialize<DateTime>(CloudFolderSyncJson.Options) != default;
    }

    private static bool HasReadableOperationStructure(CloudFolderSyncOperation operation)
    {
        return operation.SchemaVersion is 1 or 2 && Enum.IsDefined(operation.Collection) && Enum.IsDefined(operation.Kind) &&
            (operation.Kind == CloudFolderSyncOperationKind.Delete ||
             operation.Collection == UserDataSyncCollection.Dictionary && operation.Dictionary is { Original: not null } d &&
                Enum.IsDefined(d.EntryType) && d.CreatedAt != default && d.UpdatedAt != default ||
             operation.Collection == UserDataSyncCollection.Snippets && operation.Snippet is { Trigger: not null, Replacement: not null } n &&
                n.CreatedAt != default && n.UpdatedAt != default);
    }

    // A uniquely named temporary file avoids colliding with other writers, and the flushed move shows
    // readers a whole file. Shared immutable metadata uses create-once publication above.
    // macOS skips hidden files and reads only the .json extension, so the temporary file is invisible to it.
    private static void WriteJson<T>(T value, string path) =>
        AtomicFileWriter.WriteAllText(path, CloudFolderSyncJson.Serialize(value));

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
            segment is "." or ".." ||
            segment.EndsWith('.') || segment.EndsWith(' ') ||
            // Sync folders move between platforms. Linux's two separator constants
            // are both '/', so validate Windows separators and filename syntax too.
            segment.Any(character => char.IsControl(character) || "\\/<>:\"|?*".Contains(character)))
        {
            throw new ArgumentException("Expected a relative file name segment.", parameterName);
        }

        return segment;
    }

    private static string CurrentAppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
}
