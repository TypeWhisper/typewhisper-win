using System.Text.Json;
using System.Text.Json.Serialization;
using TypeWhisper.Core.Models;

namespace TypeWhisper.Core.Services.Sync;

/// <summary>Persistent folder choice and per-device synchronization progress.</summary>
public sealed record CloudFolderSyncPreferences(string? Folder = null, bool Enabled = false, CloudFolderSyncState? State = null);

/// <summary>Synchronizes isolated profile catalogs without holding profile locks during cloud I/O.</summary>
public sealed class PersistedCloudFolderSync
{
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private string PreferencesPath => Path.Combine(_root, "cloud-folder-sync.json");
    /// <summary>The saved folder and sync progress.</summary>
    public CloudFolderSyncPreferences Preferences { get; private set; }

    /// <summary>Loads preferences for this explicit profile; malformed data fails closed.</summary>
    public PersistedCloudFolderSync(string profileRoot)
    {
        _root = Path.GetFullPath(profileRoot);
        Preferences = File.Exists(PreferencesPath)
            ? JsonSerializer.Deserialize<CloudFolderSyncPreferences>(File.ReadAllText(PreferencesPath), Json)
                ?? throw new JsonException("Invalid sync preferences.") : new();
    }

    /// <summary>Changes the selected folder or pauses sync. Remote files are never removed.</summary>
    public void Configure(string? folder, bool enabled)
    {
        if (!_gate.Wait(0)) throw new InvalidOperationException("Wait for synchronization to finish.");
        try
        {
            folder = string.IsNullOrWhiteSpace(folder) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (enabled && (folder is null || !Directory.Exists(folder))) throw new IOException("Choose an available sync folder first.");
            if (folder is not null && (folder.Equals(_root, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Choose a cloud folder outside the TypeWhisper profile.");
            var changed = !string.Equals(folder, Preferences.Folder, StringComparison.OrdinalIgnoreCase);
            var next = new CloudFolderSyncPreferences(folder, enabled, changed ? new() : Preferences.State ?? new());
            Save(next); Preferences = next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exchanges operations and commits only if local catalogs still match the captured snapshot.</summary>
    /// <param name="canUseSync">Rechecked before publication so access loss cannot commit remote changes.</param>
    /// <param name="publish">Optional host dispatcher; run the action and refresh live consumers before returning.</param>
    /// <param name="ct">Cancellation prevents local publication and advances no sync state.</param>
    public async Task<CloudFolderSyncResult> SyncAsync(Func<bool> canUseSync, Func<Action, bool, Task>? publish = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var preferences = Preferences;
            if (!preferences.Enabled) throw new InvalidOperationException("Synchronization is paused.");
            if (!canUseSync()) throw new CloudFolderSyncNotEntitledException();
            if (preferences.Folder is null || !Directory.Exists(preferences.Folder)) throw new IOException("The sync folder is unavailable. Check your cloud provider.");
            var state = CloudFolderSyncJson.Deserialize<CloudFolderSyncState>(CloudFolderSyncJson.Serialize(preferences.State ?? new()))!;
            var dictionaryPath = Path.Combine(_root, "dictionary.json");
            var snippetsPath = Path.Combine(_root, "snippets.json");
            byte[]? dictionaryBytes, snippetBytes;
            BufferStore buffer;
            using (ProfileMutationCoordinator.Enter())
            {
                dictionaryBytes = Read(dictionaryPath); snippetBytes = Read(snippetsPath);
                if (dictionaryBytes is null && state.KnownLocalItemIds.Any(id => id.StartsWith("dictionary:", StringComparison.Ordinal)) ||
                    snippetBytes is null && state.KnownLocalItemIds.Any(id => id.StartsWith("snippet:", StringComparison.Ordinal)))
                    throw new IOException("A previously synchronized local catalog is missing. Restore it before syncing again.");
                buffer = new(ReadEntries<DictionaryEntry>(dictionaryBytes), ReadEntries<Snippet>(snippetBytes));
            }
            var result = await Task.Run(() => CloudFolderSyncEngine.SyncAsync(preferences.Folder, buffer, state,
                new PaidEntitlements(true), cancellationToken: ct), ct).ConfigureAwait(false);
            void Commit()
            {
                ct.ThrowIfCancellationRequested();
                if (!canUseSync()) throw new CloudFolderSyncNotEntitledException();
                using var mutation = ProfileMutationCoordinator.Enter();
                if (!Same(dictionaryBytes, Read(dictionaryPath)) || !Same(snippetBytes, Read(snippetsPath)))
                    throw new InvalidOperationException("Local entries changed during sync. They were kept; synchronize again.");
                // Atomic per catalog. If a later write fails, leave progress unchanged; replay is idempotent.
                if (buffer.DictionaryChanged) SnippetCatalogTransaction.WriteAtomically(dictionaryPath, JsonSerializer.Serialize(buffer.Dictionary, Json));
                if (buffer.SnippetsChanged) SnippetCatalogTransaction.WriteAtomically(snippetsPath, JsonSerializer.Serialize(buffer.Snippets, Json));
                var next = preferences with { State = state };
                Save(next); Preferences = next;
            }
            if (publish is null) Commit(); else await publish(Commit, result.MutationsApplied > 0).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    private void Save(CloudFolderSyncPreferences preferences) => SnippetCatalogTransaction.WriteAtomically(PreferencesPath, JsonSerializer.Serialize(preferences, Json));
    private static byte[]? Read(string path) { try { return File.ReadAllBytes(path); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; } }
    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    private static List<T> ReadEntries<T>(byte[]? bytes) => bytes is null ? [] : JsonSerializer.Deserialize<List<T>>(bytes, Json) ?? throw new JsonException("Invalid local catalog.");

    private sealed class BufferStore : IUserDataSyncStore
    {
        internal List<DictionaryEntry> Dictionary { get; }
        internal List<Snippet> Snippets { get; }
        internal bool DictionaryChanged { get; private set; }
        internal bool SnippetsChanged { get; private set; }
        internal BufferStore(List<DictionaryEntry> dictionary, List<Snippet> snippets)
        {
            if (dictionary.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Original)) ||
                snippets.Any(e => e is null || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.Trigger) || e.Replacement is null || e.Tags is null))
                throw new JsonException("Invalid local catalog. No sync was performed.");
            Dictionary = dictionary; Snippets = snippets;
        }
        private static bool Personal(DictionaryEntry e) => !e.Id.StartsWith("pack:", StringComparison.Ordinal);
        public UserDataSyncSnapshot Snapshot() => new(
            Dictionary.Where(Personal).Select(e => new UserDataSyncDictionaryEntry(
                e.EntryType == DictionaryEntryType.Term ? UserDataSyncDictionaryEntryType.Term : UserDataSyncDictionaryEntryType.Correction,
                e.Original, e.Replacement, e.CaseSensitive, e.IsEnabled, e.CreatedAt, e.UpdatedAt == default ? e.CreatedAt : e.UpdatedAt, e.Source, e.IsRegex, e.CtcMinSimilarity)).ToArray(),
            Snippets.Select(e => new UserDataSyncSnippet(e.Trigger, e.Replacement, e.CaseSensitive, e.IsEnabled,
                e.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), e.CreatedAt, e.UpdatedAt == default ? e.CreatedAt : e.UpdatedAt)).ToArray());
        public void Apply(IReadOnlyList<UserDataSyncMutation> mutations)
        {
            foreach (var mutation in mutations)
            {
                switch (mutation)
                {
                    case UserDataSyncMutation.DeleteDictionary d:
                        DictionaryChanged |= Dictionary.RemoveAll(e => Personal(e) && UserDataSyncIdentity.DictionaryItemId(e.EntryType, e.Original) == d.ItemId) > 0;
                        break;
                    case UserDataSyncMutation.DeleteSnippet d:
                        SnippetsChanged |= Snippets.RemoveAll(e => UserDataSyncIdentity.SnippetItemId(e.Trigger) == d.ItemId) > 0;
                        break;
                    case UserDataSyncMutation.UpsertDictionary u:
                        var e = u.Entry;
                        var id = UserDataSyncIdentity.DictionaryItemId(e.EntryType, e.Original);
                        var old = Dictionary.FirstOrDefault(x => Personal(x) && UserDataSyncIdentity.DictionaryItemId(x.EntryType, x.Original) == id);
                        var entry = (old ?? new DictionaryEntry { Id = Guid.NewGuid().ToString(), Original = e.Original, EntryType = e.EntryType == UserDataSyncDictionaryEntryType.Term ? DictionaryEntryType.Term : DictionaryEntryType.Correction })
                            with { Original = e.Original, Replacement = e.Replacement, CaseSensitive = e.CaseSensitive, IsEnabled = e.IsEnabled, CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt, Source = e.Source, IsRegex = e.IsRegex, CtcMinSimilarity = e.CtcMinSimilarity };
                        Dictionary.RemoveAll(x => Personal(x) && UserDataSyncIdentity.DictionaryItemId(x.EntryType, x.Original) == id);
                        Dictionary.Add(entry); DictionaryChanged = true;
                        break;
                    case UserDataSyncMutation.UpsertSnippet u:
                        var n = u.Snippet;
                        var key = UserDataSyncIdentity.SnippetItemId(n.Trigger);
                        var previous = Snippets.FirstOrDefault(x => UserDataSyncIdentity.SnippetItemId(x.Trigger) == key);
                        var next = (previous ?? new Snippet { Id = Guid.NewGuid().ToString(), Trigger = n.Trigger, Replacement = n.Replacement })
                            with { Trigger = n.Trigger, Replacement = n.Replacement, CaseSensitive = n.CaseSensitive, IsEnabled = n.IsEnabled, CreatedAt = n.CreatedAt, UpdatedAt = n.UpdatedAt, Tags = string.Join(",", n.Tags) };
                        Snippets.RemoveAll(x => UserDataSyncIdentity.SnippetItemId(x.Trigger) == key);
                        Snippets.Add(next); SnippetsChanged = true;
                        break;
                }
            }
        }
        public Guid ObserveLocalChanges(Action handler) => Guid.NewGuid();
        public void RemoveLocalChangeObserver(Guid id) { }
    }
}
