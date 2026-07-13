using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Tests.Services;

public sealed class CloudFolderSyncTests : IDisposable
{
    private readonly string _tempDir;

    public CloudFolderSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_cloud_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void DeterministicItemIdsUseNaturalKeys()
    {
        Assert.Equal(
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, " TypeWhisper "),
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, "typewhisper"));

        Assert.Equal(
            UserDataSyncIdentity.SnippetItemId("Résumé"),
            UserDataSyncIdentity.SnippetItemId("resume"));

        Assert.NotEqual(
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, "same"),
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Correction, "same"));
    }

    [Fact]
    public void ProviderDetectionFromFolderPathRecognizesCommonProviders()
    {
        Assert.Equal(CloudFolderSyncProvider.ICloudDrive, CloudFolderSyncProviderDetector.Detect(@"C:\Users\Marco\iCloudDrive\TypeWhisper"));
        Assert.Equal(CloudFolderSyncProvider.OneDrive, CloudFolderSyncProviderDetector.Detect(@"C:\Users\Marco\OneDrive - Example\TypeWhisper"));
        Assert.Equal(CloudFolderSyncProvider.Dropbox, CloudFolderSyncProviderDetector.Detect(@"C:\Users\Marco\Dropbox\TypeWhisper"));
        Assert.Equal(CloudFolderSyncProvider.Custom, CloudFolderSyncProviderDetector.Detect(@"D:\Sync\TypeWhisper"));
    }

    [Fact]
    public async Task UnentitledSyncDoesNotCreateFiles()
    {
        var store = new InMemoryUserDataSyncStore(dictionaryEntries:
        [
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10))
        ]);
        var state = new CloudFolderSyncState { DeviceId = "win-a" };

        await Assert.ThrowsAsync<CloudFolderSyncNotEntitledException>(() =>
            CloudFolderSyncEngine.SyncAsync(
                _tempDir,
                store,
                state,
                new PaidEntitlements(CanUseCloudFolderSync: false),
                now: Date(20)));

        Assert.False(Directory.Exists(CloudFolderSyncEngine.PackagePath(_tempDir)));
    }

    [Fact]
    public void ExportCollapsesDuplicateNaturalKeysToNewestRecord()
    {
        var older = Snippet(trigger: ";SIG", replacement: "Old", updatedAt: Date(10));
        var newer = Snippet(trigger: ";sig", replacement: "New", updatedAt: Date(20));

        var records = CloudFolderSyncEngine.RecordsFrom(new UserDataSyncSnapshot(Snippets: [older, newer]));

        var itemId = UserDataSyncIdentity.SnippetItemId(";sig");
        Assert.Single(records);
        Assert.Equal("New", records[itemId].Snippet?.Replacement);
    }

    [Fact]
    public async Task OperationEncodingPreservesFractionalSeconds()
    {
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_010_456).UtcDateTime;
        var deviceAStore = new InMemoryUserDataSyncStore(dictionaryEntries:
        [
            DictionaryEntry(original: "TypeWhisper", updatedAt: updatedAt)
        ]);
        var deviceBStore = new InMemoryUserDataSyncStore();
        var deviceAState = new CloudFolderSyncState { DeviceId = "win-a" };
        var deviceBState = new CloudFolderSyncState { DeviceId = "win-b" };

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceAStore,
            deviceAState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(20));

        var operationFile = Assert.Single(OperationFiles("win-a"));
        var operationJson = File.ReadAllText(operationFile);
        Assert.Contains(".456", operationJson);

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceBStore,
            deviceBState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(30));

        var syncedUpdatedAt = Assert.Single(deviceBStore.DictionaryEntries).UpdatedAt;
        Assert.Equal(updatedAt, syncedUpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task MalformedOperationFileIsSkipped()
    {
        var remoteDirectory = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "remote-device");
        Directory.CreateDirectory(remoteDirectory);
        File.WriteAllText(Path.Combine(remoteDirectory, "bad.json"), "not-json");

        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "win-a" };

        var result = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            store,
            state,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(20));

        Assert.Equal(0, result.MutationsApplied);
    }

    [Fact]
    public async Task InvalidDeviceIdIsRejectedBeforeCreatingDeviceFolder()
    {
        var store = new InMemoryUserDataSyncStore(dictionaryEntries:
        [
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10))
        ]);
        var state = new CloudFolderSyncState { DeviceId = @"..\outside" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CloudFolderSyncEngine.SyncAsync(
                _tempDir,
                store,
                state,
                new PaidEntitlements(CanUseCloudFolderSync: true),
                now: Date(20)));

        Assert.False(Directory.Exists(Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops")));
    }


    [Fact]
    public async Task TwoSimulatedDevicesShareAppendOnlyOperations()
    {
        var firstEntry = DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10));
        var deviceAStore = new InMemoryUserDataSyncStore(dictionaryEntries: [firstEntry]);
        var deviceBStore = new InMemoryUserDataSyncStore();
        var deviceAState = new CloudFolderSyncState { DeviceId = "win-a" };
        var deviceBState = new CloudFolderSyncState { DeviceId = "win-b" };

        var firstResult = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceAStore,
            deviceAState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(20));
        var secondResult = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceBStore,
            deviceBState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(30));

        Assert.Equal(1, firstResult.OperationsWritten);
        Assert.Equal(1, secondResult.MutationsApplied);
        Assert.Equal("TypeWhisper", Assert.Single(deviceBStore.DictionaryEntries).Original);
        Assert.True(Directory.Exists(Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "win-a")));
    }

    [Fact]
    public async Task DeleteTombstoneWinsOverOlderLocalItem()
    {
        var snippet = Snippet(trigger: ";sig", replacement: "Regards", updatedAt: Date(10));
        var deviceAStore = new InMemoryUserDataSyncStore(snippets: [snippet]);
        var deviceAState = new CloudFolderSyncState { DeviceId = "win-a" };

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceAStore,
            deviceAState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(20));

        deviceAStore.Snippets.Clear();
        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceAStore,
            deviceAState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(30));

        var deviceBStore = new InMemoryUserDataSyncStore(snippets: [snippet]);
        var deviceBState = new CloudFolderSyncState { DeviceId = "win-b" };
        var result = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceBStore,
            deviceBState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(40));

        Assert.Equal(1, result.MutationsApplied);
        Assert.Empty(deviceBStore.Snippets);
    }

    [Fact]
    public async Task AlreadyAppliedRemoteOperationIsNotAppliedAgain()
    {
        var entry = DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10));
        var deviceAStore = new InMemoryUserDataSyncStore(dictionaryEntries: [entry]);
        var deviceBStore = new InMemoryUserDataSyncStore();
        var deviceAState = new CloudFolderSyncState { DeviceId = "win-z" };
        var deviceBState = new CloudFolderSyncState { DeviceId = "win-b" };

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceAStore,
            deviceAState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(20));

        var firstResult = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceBStore,
            deviceBState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(30));
        var secondResult = await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            deviceBStore,
            deviceBState,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(40));

        Assert.Equal(1, firstResult.MutationsApplied);
        Assert.Equal(0, secondResult.MutationsApplied);
        Assert.Single(deviceBStore.AppliedMutations);
    }

    [Fact]
    public async Task ExpiredLocalTombstonesArePrunedAfterRetentionWindow()
    {
        var itemId = UserDataSyncIdentity.SnippetItemId(";sig");
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState
        {
            DeviceId = "win-a",
            KnownLocalItemIds = [itemId]
        };

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            store,
            state,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(10));
        Assert.Single(OperationFiles("win-a"));

        await CloudFolderSyncEngine.SyncAsync(
            _tempDir,
            store,
            state,
            new PaidEntitlements(CanUseCloudFolderSync: true),
            now: Date(10 + 91 * 24 * 60 * 60));

        Assert.Empty(OperationFiles("win-a"));
    }

    [Fact]
    public void ConflictTieBreakerUsesUpdatedAtThenDeviceId()
    {
        var itemId = UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Term, "TypeWhisper");
        var older = CloudFolderSyncOperation.UpsertDictionary(
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10)),
            itemId,
            "win-z",
            operationId: "older");
        var newer = CloudFolderSyncOperation.UpsertDictionary(
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(20)),
            itemId,
            "win-a",
            operationId: "newer");
        var sameTimeHigherDevice = CloudFolderSyncOperation.UpsertDictionary(
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(20)),
            itemId,
            "win-z",
            operationId: "tie");

        var winner = Assert.Single(CloudFolderSyncEngine.WinningOperations([older, newer, sameTimeHigherDevice]).Values);
        Assert.Equal("tie", winner.OperationId);
    }

    [Fact]
    public void OperationJsonUsesCamelCaseStringEnumsAndMacCompatibleNames()
    {
        var operation = CloudFolderSyncOperation.UpsertDictionary(
            DictionaryEntry(
                entryType: UserDataSyncDictionaryEntryType.Correction,
                original: "teh",
                replacement: "",
                source: DictionaryEntrySource.AutoLearned,
                updatedAt: Date(10)),
            UserDataSyncIdentity.DictionaryItemId(UserDataSyncDictionaryEntryType.Correction, "teh"),
            "win-a",
            operationId: "op-1");

        var json = CloudFolderSyncJson.Serialize(operation);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.Equal("dictionary", root.GetProperty("collection").GetString());
        Assert.Equal("upsert", root.GetProperty("kind").GetString());
        Assert.Equal("correction", root.GetProperty("dictionary").GetProperty("entryType").GetString());
        Assert.Equal("", root.GetProperty("dictionary").GetProperty("replacement").GetString());
        Assert.Equal("autoLearned", root.GetProperty("dictionary").GetProperty("source").GetString());

        var roundTrip = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(json);
        Assert.Equal(DictionaryEntrySource.AutoLearned, roundTrip!.Dictionary!.Source);

        var unknownSource = json.Replace("autoLearned", "futureValue", StringComparison.Ordinal);
        roundTrip = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(unknownSource);
        Assert.Equal(DictionaryEntrySource.Manual, roundTrip!.Dictionary!.Source);
    }

    private IReadOnlyList<string> OperationFiles(string deviceId)
    {
        var directory = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", deviceId);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").ToArray()
            : [];
    }

    private static UserDataSyncDictionaryEntry DictionaryEntry(
        string original,
        DateTime updatedAt,
        UserDataSyncDictionaryEntryType entryType = UserDataSyncDictionaryEntryType.Term,
        string? replacement = null,
        DictionaryEntrySource source = DictionaryEntrySource.Manual) =>
        new(
            entryType,
            original,
            replacement,
            CaseSensitive: false,
            IsEnabled: true,
            CreatedAt: Date(1),
            updatedAt,
            source);

    private static UserDataSyncSnippet Snippet(string trigger, string replacement, DateTime updatedAt) =>
        new(
            trigger,
            replacement,
            CaseSensitive: false,
            IsEnabled: true,
            Tags: [],
            CreatedAt: Date(1),
            updatedAt);

    private static DateTime Date(double seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + (long)(seconds * 1000)).UtcDateTime;

    private sealed class InMemoryUserDataSyncStore : IUserDataSyncStore
    {
        public List<UserDataSyncDictionaryEntry> DictionaryEntries { get; }
        public List<UserDataSyncSnippet> Snippets { get; }
        public List<UserDataSyncMutation> AppliedMutations { get; } = [];

        public InMemoryUserDataSyncStore(
            IEnumerable<UserDataSyncDictionaryEntry>? dictionaryEntries = null,
            IEnumerable<UserDataSyncSnippet>? snippets = null)
        {
            DictionaryEntries = dictionaryEntries?.ToList() ?? [];
            Snippets = snippets?.ToList() ?? [];
        }

        public UserDataSyncSnapshot Snapshot() => new(DictionaryEntries, Snippets);

        public void Apply(IReadOnlyList<UserDataSyncMutation> mutations)
        {
            AppliedMutations.AddRange(mutations);

            foreach (var mutation in mutations)
            {
                switch (mutation)
                {
                    case UserDataSyncMutation.UpsertDictionary upsert:
                        var dictionaryItemId = UserDataSyncIdentity.DictionaryItemId(upsert.Entry.EntryType, upsert.Entry.Original);
                        DictionaryEntries.RemoveAll(entry =>
                            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == dictionaryItemId);
                        DictionaryEntries.Add(upsert.Entry);
                        break;

                    case UserDataSyncMutation.DeleteDictionary delete:
                        DictionaryEntries.RemoveAll(entry =>
                            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original) == delete.ItemId);
                        break;

                    case UserDataSyncMutation.UpsertSnippet upsert:
                        var snippetItemId = UserDataSyncIdentity.SnippetItemId(upsert.Snippet.Trigger);
                        Snippets.RemoveAll(snippet =>
                            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == snippetItemId);
                        Snippets.Add(upsert.Snippet);
                        break;

                    case UserDataSyncMutation.DeleteSnippet delete:
                        Snippets.RemoveAll(snippet =>
                            UserDataSyncIdentity.SnippetItemId(snippet.Trigger) == delete.ItemId);
                        break;
                }
            }
        }

        public Guid ObserveLocalChanges(Action handler) => Guid.NewGuid();
        public void RemoveLocalChangeObserver(Guid id) { }
    }
}
