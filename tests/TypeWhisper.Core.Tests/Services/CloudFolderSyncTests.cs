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
    public async Task DictionarySyncKeepsTheHistoryDeviceName()
    {
        // History sync names the device first; the next dictionary sync must not erase what macOS shows.
        HistoryFolderSync.Sync(_tempDir, "win-a", new HistorySyncState { Enabled = true, HistoryDeviceId = "history-a" }, [], "Test PC", "1.1.0", Date(10));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, new InMemoryUserDataSyncStore(), new CloudFolderSyncState { DeviceId = "win-a" },
            new PaidEntitlements(CanUseCloudFolderSync: true), now: Date(20));

        var device = JsonDocument.Parse(File.ReadAllText(Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "devices", "win-a.json"))).RootElement;
        Assert.Equal("Test PC", device.GetProperty("name").GetString());
        Assert.Equal("history-a", device.GetProperty("historyOriginDeviceID").GetString());
        Assert.Equal("win-a", device.GetProperty("deviceId").GetString());
        Assert.Equal(Date(20), device.GetProperty("updatedAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void ChoosingTheSyncPackageItselfUsesItsParent()
    {
        var package = CloudFolderSyncEngine.PackagePath(_tempDir);
        Directory.CreateDirectory(package);
        // An empty folder of that name is not a package yet, so it is kept.
        Assert.Equal(package, CloudFolderSyncEngine.SyncFolder(package));

        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}");
        Assert.Equal(_tempDir, CloudFolderSyncEngine.SyncFolder(package));
        Assert.Equal(_tempDir, CloudFolderSyncEngine.SyncFolder(package + Path.DirectorySeparatorChar));
        Assert.Equal(_tempDir, CloudFolderSyncEngine.SyncFolder(_tempDir));
    }

    [Fact]
    public async Task DictionaryOnlyDeviceIsNamed()
    {
        await CloudFolderSyncEngine.SyncAsync(_tempDir, new InMemoryUserDataSyncStore(), new CloudFolderSyncState { DeviceId = "win-b" },
            new PaidEntitlements(CanUseCloudFolderSync: true), now: Date(20));

        var device = JsonDocument.Parse(File.ReadAllText(Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "devices", "win-b.json"))).RootElement;
        Assert.Equal(Environment.MachineName, device.GetProperty("name").GetString());
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

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData("../outside")]
    [InlineData(@"nested\device")]
    [InlineData("nested/device")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"C:\outside")]
    [InlineData("C:outside")]
    [InlineData("device:stream")]
    [InlineData("device?")]
    [InlineData("device.")]
    [InlineData("device ")]
    [InlineData("device\u0000")]
    public async Task InvalidDeviceIdIsRejectedBeforeCreatingDeviceFolder(string deviceId)
    {
        var store = new InMemoryUserDataSyncStore(dictionaryEntries:
        [
            DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10))
        ]);
        var state = new CloudFolderSyncState { DeviceId = deviceId };

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
    public async Task PassSucceedsWhileAnotherDeviceStillWritesItsTemporaryManifest()
    {
        var package = CloudFolderSyncEngine.PackagePath(_tempDir);
        Directory.CreateDirectory(package);
        // Another device is mid-write: the obvious temporary name exists and is locked.
        using var foreign = new FileStream(Path.Combine(package, "manifest.json.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await CloudFolderSyncEngine.SyncAsync(_tempDir, new InMemoryUserDataSyncStore(), new CloudFolderSyncState { DeviceId = "win-a" },
            new PaidEntitlements(CanUseCloudFolderSync: true), now: Date(20));

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "manifest.json"))).RootElement;
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["manifest.json.tmp"], Directory.GetFiles(package, "*.tmp").Select(Path.GetFileName));
    }

    [Fact]
    public async Task ExistingManifestIsPreservedWhileDeviceTimestampIsUpdated()
    {
        var package = CloudFolderSyncEngine.PackagePath(_tempDir);
        Directory.CreateDirectory(package);
        var path = Path.Combine(package, "manifest.json");
        const string original = """
            {"schemaVersion":1,"createdBy":"TypeWhisper","updatedAt":"2025-01-01T00:00:00Z","futureField":"preserve"}
            """;
        File.WriteAllText(path, original);

        await CloudFolderSyncEngine.SyncAsync(_tempDir, new InMemoryUserDataSyncStore(), new CloudFolderSyncState { DeviceId = "win-a" },
            new PaidEntitlements(CanUseCloudFolderSync: true), now: Date(20));

        Assert.Equal(original, File.ReadAllText(path));
        using var device = JsonDocument.Parse(File.ReadAllText(Path.Combine(package, "devices", "win-a.json")));
        Assert.Equal(Date(20), device.RootElement.GetProperty("updatedAt").GetDateTime());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"createdBy\":\"TypeWhisper\"}", true)]
    [InlineData("{\"schemaVersion\":1,\"createdBy\":\"AnotherApp\"}", true)]
    [InlineData("not json", false)]
    public async Task IncompatibleOrUnreadableManifestIsNeverOverwritten(string original, bool unsupportedFormat)
    {
        var package = CloudFolderSyncEngine.PackagePath(_tempDir);
        Directory.CreateDirectory(package);
        var path = Path.Combine(package, "manifest.json");
        File.WriteAllText(path, original);

        var error = await Assert.ThrowsAsync<CloudFolderSyncManifestException>(() => CloudFolderSyncEngine.SyncAsync(_tempDir,
            new InMemoryUserDataSyncStore(), new CloudFolderSyncState { DeviceId = "win-a" },
            new PaidEntitlements(CanUseCloudFolderSync: true), now: Date(20)));

        Assert.Equal(unsupportedFormat, error.UnsupportedFormat);
        Assert.Contains(path, error.Message);
        Assert.Contains("choose another sync folder", error.Message);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(package, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConcurrentPassesOfTwoDevicesOnOneFolderNeverCollide()
    {
        var entitlements = new PaidEntitlements(CanUseCloudFolderSync: true);
        var deviceAStore = new InMemoryUserDataSyncStore(dictionaryEntries: [DictionaryEntry(original: "TypeWhisper", updatedAt: Date(10))]);
        var deviceBStore = new InMemoryUserDataSyncStore();
        var deviceAState = new CloudFolderSyncState { DeviceId = "win-a" };
        var deviceBState = new CloudFolderSyncState { DeviceId = "win-b" };

        for (var round = 0; round < 10; round++)
        {
            var now = Date(20 + round);
            await Task.WhenAll(
                Task.Run(() => CloudFolderSyncEngine.SyncAsync(_tempDir, deviceAStore, deviceAState, entitlements, now)),
                Task.Run(() => CloudFolderSyncEngine.SyncAsync(_tempDir, deviceBStore, deviceBState, entitlements, now)));
        }

        Assert.Equal("TypeWhisper", Assert.Single(deviceBStore.DictionaryEntries).Original);
        Assert.Empty(Directory.EnumerateFiles(CloudFolderSyncEngine.PackagePath(_tempDir), "*.tmp", SearchOption.AllDirectories));
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
    public async Task OldTombstonesRemainUntilSafeCompactionIsAvailable()
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

        Assert.Single(OperationFiles("win-a"));
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

    [Theory]
    [InlineData("upsert-dictionary-v1.json", "recieve", "receive")]
    [InlineData("upsert-dictionary-ctc-v1.json", "TypeWhisper", null)]
    public async Task ImportsMacDictionaryFixtureWithoutRepeatingMutation(string fixture, string original, string? replacement)
    {
        StageMacFixture(fixture);
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "fixture-windows" };
        var entitlements = new PaidEntitlements(CanUseCloudFolderSync: true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlements);
        var entry = Assert.Single(store.DictionaryEntries);
        Assert.Equal(original, entry.Original);
        Assert.Equal(replacement, entry.Replacement);
        store.AppliedMutations.Clear();
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlements);
        Assert.Empty(store.AppliedMutations);
    }

    [Fact]
    public async Task ImportsMacLegacySnippetWithoutTags()
    {
        StageMacFixture("upsert-snippet-legacy-v1.json");
        var store = new InMemoryUserDataSyncStore();
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store,
            new CloudFolderSyncState { DeviceId = "fixture-windows" },
            new PaidEntitlements(CanUseCloudFolderSync: true));
        var snippet = Assert.Single(store.Snippets);
        Assert.Equal(";hello", snippet.Trigger);
        Assert.Equal("Hello!", snippet.Replacement);
        Assert.Empty(snippet.Tags);
    }

    private void StageMacFixture(string name)
    {
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "fixture-mac");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", name), Path.Combine(folder, name));
    }

    private IReadOnlyList<string> OperationFiles(string deviceId)
    {
        var directory = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", deviceId);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").ToArray()
            : [];
    }

    [Fact]
    public void RegexIdentitiesAndOperationsMatchSharedMacFixtures()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync");
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "regex-dictionary-identities-v2.json")));
        foreach (var vector in vectors.RootElement.EnumerateArray())
            Assert.Equal(vector.GetProperty("itemId").GetString(), UserDataSyncIdentity.DictionaryItemId(
                UserDataSyncDictionaryEntryType.Correction, vector.GetProperty("pattern").GetString()!, true));
        var operation = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(Path.Combine(folder, "upsert-dictionary-regex-v2.json")))!;
        Assert.Equal(2, operation.SchemaVersion);
        Assert.Equal(operation, CloudFolderSyncEngine.WinningOperations([operation])[operation.ItemId]);
        var deletion = CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, operation.ItemId, "mac", Date(30));
        Assert.Equal(2, deletion.SchemaVersion);
        Assert.Equal(CloudFolderSyncOperationKind.Delete, CloudFolderSyncEngine.WinningOperations([operation, deletion])[operation.ItemId].Kind);
        // Pre-upgrade clients only accept schema 1, so cannot fold or overwrite these entries.
        Assert.DoesNotContain(new[] { operation, deletion }, item => item.SchemaVersion == 1);
    }

    [Fact]
    public async Task RegexPatternsAndLiteralEntriesRoundTripAndDeleteIndependently()
    {
        var first = new InMemoryUserDataSyncStore(dictionaryEntries: [
            DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "digit") with { IsRegex = true },
            DictionaryEntry(@"\D", Date(10), UserDataSyncDictionaryEntryType.Correction, "other") with { IsRegex = true },
            DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "literal")
        ]);
        var second = new InMemoryUserDataSyncStore();
        var a = new CloudFolderSyncState { DeviceId = "windows" };
        var b = new CloudFolderSyncState { DeviceId = "mac" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(20));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, second, b, entitlement, Date(30));
        Assert.Equal(3, second.DictionaryEntries.Count);
        second.DictionaryEntries.RemoveAll(entry => entry.IsRegex && entry.Original == @"\D");
        await CloudFolderSyncEngine.SyncAsync(_tempDir, second, b, entitlement, Date(40));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(50));
        Assert.Equal(2, first.DictionaryEntries.Count);
        Assert.All(first.DictionaryEntries, entry => Assert.Equal(@"\d", entry.Original));
        Assert.Single(first.DictionaryEntries, entry => entry.IsRegex);
    }

    [Fact]
    public async Task LiteralToRegexBeforeFirstUpgradeSyncDeletesTheOldLiteral()
    {
        var entry = DictionaryEntry("foo", Date(10), UserDataSyncDictionaryEntryType.Correction, "bar");
        var oldId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
        var legacy = CloudFolderSyncOperation.UpsertDictionary(entry, oldId, "legacy", "old-literal");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        CloudFolderSyncState OldState(string device) => new()
        {
            DeviceId = device, KnownLocalItemIds = [oldId], AppliedOperationIds = [legacy.OperationId],
            ExportedItemVersions = new() { [oldId] = "1700000010000" }
        };
        var a = OldState("a");
        var b = OldState("b");
        var first = new InMemoryUserDataSyncStore(dictionaryEntries: [entry]);
        var changed = new InMemoryUserDataSyncStore(dictionaryEntries: [entry with { IsRegex = true, UpdatedAt = Date(20) }]);
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(30));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, changed, b, entitlement, Date(40));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(50));
        Assert.True(Assert.Single(first.DictionaryEntries).IsRegex);
        Assert.True(Assert.Single(changed.DictionaryEntries).IsRegex);
        Assert.Contains(OperationFiles("b").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!),
            operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == oldId && operation.SchemaVersion == 1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RemovingRegexBeforeFirstUpgradeSyncDeletesItsMigratedIdentity(bool renamePattern, bool laterLegacyDelete, bool convertToLiteral)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        var entry = legacy.Dictionary!;
        CloudFolderSyncState OldState(string device) => new()
        {
            DeviceId = device, KnownLocalItemIds = [legacy.ItemId], AppliedOperationIds = [legacy.OperationId],
            ExportedItemVersions = new() { [legacy.ItemId] = "1700000010000" }
        };
        var a = OldState("a");
        var b = OldState("b");
        var first = new InMemoryUserDataSyncStore(dictionaryEntries: [entry]);
        var removed = new InMemoryUserDataSyncStore(dictionaryEntries: renamePattern
            ? [entry with { Original = @"\D+", UpdatedAt = Date(35) }]
            : convertToLiteral ? [entry with { IsRegex = false, UpdatedAt = Date(35) }] : []);
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(30));
        if (laterLegacyDelete)
            File.WriteAllText(Path.Combine(folder, "other-delete.json"), CloudFolderSyncJson.Serialize(
                CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, legacy.ItemId, "old-peer", Date(35))));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, removed, b, entitlement, Date(40));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, first, a, entitlement, Date(50));
        foreach (var store in new[] { first, removed })
        {
            Assert.DoesNotContain(store.DictionaryEntries, item => item.IsRegex && item.Original == entry.Original);
            Assert.Equal(renamePattern || convertToLiteral ? 1 : 0, store.DictionaryEntries.Count);
            if (renamePattern) Assert.Equal(@"\D+", Assert.Single(store.DictionaryEntries).Original);
            if (convertToLiteral) Assert.False(Assert.Single(store.DictionaryEntries).IsRegex);
        }
        var rawId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, true);
        var published = OperationFiles("b").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Equal(!convertToLiteral, published.Any(operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == legacy.ItemId && operation.SchemaVersion == 1));
        Assert.Contains(published, operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == rawId && operation.SchemaVersion == 2);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, removed, b, entitlement, Date(60))).OperationsWritten);
    }

    [Fact]
    public async Task UpgradingSyncedRegexDoesNotDeleteItOnLegacyPeers()
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        var entry = legacy.Dictionary!;
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [entry]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows",
            KnownLocalItemIds = [legacy.ItemId],
            ExportedItemVersions = new() { [legacy.ItemId] = "1700000010000" },
            AppliedOperationIds = [legacy.OperationId]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        var published = OperationFiles("windows").Select(path =>
            CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Equal(2, Assert.Single(published).SchemaVersion);
        Assert.DoesNotContain(legacy.ItemId, state.KnownLocalItemIds);
        // An old peer only sees v1 operations: the pre-upgrade rule must still win.
        Assert.Equal(legacy, CloudFolderSyncEngine.WinningOperations(
            published.Append(legacy).Where(operation => operation.SchemaVersion == 1))[legacy.ItemId]);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);

        // After migration, a literal with the same folded ID can be created and really deleted.
        store.DictionaryEntries.Add(entry with { IsRegex = false, UpdatedAt = Date(50) });
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(55));
        store.DictionaryEntries.RemoveAll(item => !item.IsRegex);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(60));
        Assert.Contains(OperationFiles("windows").Select(path =>
            CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!),
            operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == legacy.ItemId);
        Assert.True(Assert.Single(store.DictionaryEntries).IsRegex);
    }

    [Fact]
    public async Task LegacyRegexMigrationRepublishesV2AndKeepsFoldedTombstonesSeparate()
    {
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json"));
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(fixture)!;
        File.WriteAllText(Path.Combine(folder, "legacy.json"), fixture);
        File.WriteAllText(Path.Combine(folder, "delete.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, legacy.ItemId, "legacy", Date(5))));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        Assert.True(Assert.Single(store.DictionaryEntries).IsRegex);
        var republish = await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Equal(1, republish.OperationsWritten);
        Assert.Equal(2, CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(Assert.Single(OperationFiles("windows")) ))!.SchemaVersion);
        var repeated = await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(35));
        Assert.Equal(0, repeated.OperationsWritten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTombstonePreventsResurrectionButDoesNotDeleteV2Rule(bool unencodedLegacyId)
    {
        var fixtureFolder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync");
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(Path.Combine(fixtureFolder, "upsert-dictionary-regex-v1.json")))!;
        if (unencodedLegacyId) legacy = legacy with { ItemId = @"dictionary:correction:\d+" };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        File.WriteAllText(Path.Combine(folder, "delete.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, legacy.ItemId, "legacy", Date(20))));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        Assert.Empty(store.DictionaryEntries);
        File.Copy(Path.Combine(fixtureFolder, "upsert-dictionary-regex-v2.json"), Path.Combine(folder, "v2.json"));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.True(Assert.Single(store.DictionaryEntries).IsRegex);
    }

    [Fact]
    public async Task UnencodedLegacyUpdateDoesNotStealCoexistingLiteral()
    {
        var regex = DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "regex") with { IsRegex = true };
        var literal = regex with { IsRegex = false, Replacement = "literal" };
        var legacyEntry = literal with { Replacement = "updated", UpdatedAt = Date(20), IsRegexFieldPresent = false };
        var operation = CloudFolderSyncOperation.UpsertDictionary(legacyEntry, @"dictionary:correction:\d", "legacy");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(operation));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [regex, literal]);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, new PaidEntitlements(true), Date(30));
        Assert.Equal(2, store.DictionaryEntries.Count);
        Assert.Equal("regex", Assert.Single(store.DictionaryEntries, entry => entry.IsRegex).Replacement);
        Assert.Equal("updated", Assert.Single(store.DictionaryEntries, entry => !entry.IsRegex).Replacement);
    }

    [Fact]
    public async Task LegacyMissingModePreservesRegexAndRepublishesOnce()
    {
        var entry = DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "#") with { IsRegex = true };
        var legacyEntry = entry with { IsRegex = false, IsRegexFieldPresent = false, Replacement = "new", UpdatedAt = Date(20) };
        var legacy = CloudFolderSyncOperation.UpsertDictionary(legacyEntry,
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original), "legacy", "legacy");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var json = CloudFolderSyncJson.Serialize(legacy);
        Assert.DoesNotContain("isRegex", json);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), json);
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [entry]);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        Assert.True(Assert.Single(store.DictionaryEntries).IsRegex);
        Assert.Equal("new", store.DictionaryEntries[0].Replacement);
        var republish = await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        var repeated = await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(35));
        Assert.Equal(1, republish.OperationsWritten);
        Assert.Equal(0, repeated.OperationsWritten);
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
                        var dictionaryItemId = UserDataSyncIdentity.DictionaryItemId(upsert.Entry.EntryType, upsert.Entry.Original, upsert.Entry.IsRegex);
                        DictionaryEntries.RemoveAll(entry =>
                            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex) == dictionaryItemId);
                        DictionaryEntries.Add(upsert.Entry with { IsRegexFieldPresent = true });
                        break;

                    case UserDataSyncMutation.DeleteDictionary delete:
                        DictionaryEntries.RemoveAll(entry =>
                            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex) == delete.ItemId);
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
