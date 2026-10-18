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
    public async Task MigratedRegexEditsDoNotRepublishBecauseOfOldOperationFiles()
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(20));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        var updated = legacy.Dictionary! with { Replacement = "edited", UpdatedAt = Date(30) };
        var operation = CloudFolderSyncOperation.UpsertDictionary(updated,
            UserDataSyncIdentity.DictionaryItemId(updated.EntryType, updated.Original, true), "peer");
        File.WriteAllText(Path.Combine(folder, "edit.json"), CloudFolderSyncJson.Serialize(operation));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40));
        Assert.Equal("edited", Assert.Single(store.DictionaryEntries).Replacement);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(50))).OperationsWritten);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RecoveryPreservesLocalModeChanges(bool recovery, bool previouslyRegex)
    {
        var previous = DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "old") with { IsRegex = previouslyRegex };
        var oldId = UserDataSyncIdentity.DictionaryItemId(previous.EntryType, previous.Original);
        var synced = CloudFolderSyncOperation.UpsertDictionary(previous, oldId, "windows", "own-rule") with { SchemaVersion = 1 };
        var peer = synced with { DeviceId = "peer", OperationId = "peer-republished-rule" };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "peer.json"), CloudFolderSyncJson.Serialize(peer));
        if (!recovery) File.WriteAllText(Path.Combine(folder, "own.json"), CloudFolderSyncJson.Serialize(synced));
        var regex = previous with { IsRegex = true, Replacement = "current", UpdatedAt = Date(20) };
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [regex]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [oldId],
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [synced.OperationId]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Equal(regex, Assert.Single(store.DictionaryEntries));
        var published = OperationFiles("windows").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Equal(recovery || !previouslyRegex, published.Any(operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == oldId));
        Assert.Contains(published, operation => operation.SchemaVersion == 2 && operation.Dictionary == regex);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
        Assert.Equal(regex, Assert.Single(store.DictionaryEntries));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyModeChangeDoesNotRestoreSupersededRule(bool latestIsRegex, bool unencodedId)
    {
        var previous = DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "old") with { IsRegex = !latestIsRegex };
        var current = previous with { IsRegex = latestIsRegex, Replacement = "current", UpdatedAt = Date(20) };
        var oldId = unencodedId ? @"dictionary:correction:\d" : UserDataSyncIdentity.DictionaryItemId(previous.EntryType, previous.Original);
        var old = CloudFolderSyncOperation.UpsertDictionary(previous, oldId, "old", "previous") with { SchemaVersion = 1 };
        var newer = CloudFolderSyncOperation.UpsertDictionary(current, oldId, "old", "current") with { SchemaVersion = 1 };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "old");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "previous.json"), CloudFolderSyncJson.Serialize(old));
        File.WriteAllText(Path.Combine(folder, "current.json"), CloudFolderSyncJson.Serialize(newer));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Equal(current, Assert.Single(store.DictionaryEntries));
        Assert.Contains(old.OperationId, state.AppliedOperationIds);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40));
        File.Delete(Path.Combine(folder, "current.json"));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(50));
        Assert.Equal(current, Assert.Single(store.DictionaryEntries));
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, null)]
    [InlineData(1, 10001)]
    [InlineData(2, 10001)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 10000)]
    [InlineData(2, 10000)]
    public async Task RegexSyncReplacementMustFitCatalog(int schemaVersion, int? length)
    {
        var entry = DictionaryEntry(@"\d", Date(20), UserDataSyncDictionaryEntryType.Correction,
            length is { } count ? new string('x', count) : null) with { IsRegex = true };
        var operation = CloudFolderSyncOperation.UpsertDictionary(entry,
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, schemaVersion == 2), "peer") with { SchemaVersion = schemaVersion };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "rule.json"), CloudFolderSyncJson.Serialize(operation));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, new PaidEntitlements(true), Date(30));
        if (length is >= 0 and <= 10000) Assert.Equal(entry, Assert.Single(store.DictionaryEntries));
        else { Assert.Empty(store.DictionaryEntries); Assert.DoesNotContain(operation.OperationId, state.AppliedOperationIds); }
    }

    [Theory]
    [InlineData("snippet")]
    [InlineData("term")]
    [InlineData("literal")]
    [InlineData("exactRegex")]
    public async Task PackageRecoveryPreservesKnownLocalDeletions(string kind)
    {
        var local = DictionaryEntry("teh", Date(10), UserDataSyncDictionaryEntryType.Correction, "the");
        var oldId = UserDataSyncIdentity.DictionaryItemId(local.EntryType, local.Original);
        CloudFolderSyncOperation removed;
        if (kind == "snippet")
        {
            var snippet = Snippet(";sig", "Signature", Date(10));
            removed = CloudFolderSyncOperation.UpsertSnippet(snippet, UserDataSyncIdentity.SnippetItemId(snippet.Trigger), "peer");
        }
        else
        {
            var entry = DictionaryEntry("removed", Date(10), kind == "term" ? UserDataSyncDictionaryEntryType.Term : UserDataSyncDictionaryEntryType.Correction,
                kind == "term" ? null : "replacement") with { IsRegex = kind == "exactRegex" };
            removed = CloudFolderSyncOperation.UpsertDictionary(entry, UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex), "peer");
        }
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recreated.json"), CloudFolderSyncJson.Serialize(removed));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [local]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [oldId, removed.ItemId],
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z", [removed.ItemId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = ["removed-local-upsert", "removed-peer-upsert"]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Equal(local, Assert.Single(store.DictionaryEntries));
        Assert.Empty(store.Snippets);
        var published = OperationFiles("windows").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Contains(published, operation => operation.Kind == CloudFolderSyncOperationKind.Delete && operation.ItemId == removed.ItemId);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
        Assert.Equal(local, Assert.Single(store.DictionaryEntries));
        Assert.Empty(store.Snippets);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpgradeUsesExportedCorrectionVersionDespiteClockSkew(bool isRegex, bool locallyDeleted)
    {
        var entry = DictionaryEntry(@"\d", Date(10), UserDataSyncDictionaryEntryType.Correction, "current") with { IsRegex = isRegex };
        var oldId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
        var current = CloudFolderSyncOperation.UpsertDictionary(entry, oldId, "old", "current-upsert") with { SchemaVersion = 1 };
        var old = CloudFolderSyncOperation.UpsertDictionary(entry with { IsRegex = !isRegex, UpdatedAt = Date(15), Replacement = "old" }, oldId, "old", "older-upsert-future-clock") with { SchemaVersion = 1 };
        var deleted = CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, oldId, "old", Date(20));
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "old");
        Directory.CreateDirectory(folder);
        foreach (var operation in new[] { old, deleted, current })
            File.WriteAllText(Path.Combine(folder, operation.OperationId + ".json"), CloudFolderSyncJson.Serialize(operation));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: locallyDeleted ? [] : [entry]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [oldId],
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [old.OperationId, deleted.OperationId, current.OperationId]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        if (locallyDeleted) Assert.Empty(store.DictionaryEntries);
        else Assert.Equal(entry, Assert.Single(store.DictionaryEntries));
        var published = OperationFiles("windows").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Equal(locallyDeleted, published.Any(operation => operation.Kind == CloudFolderSyncOperationKind.Delete));
        if (locallyDeleted) Assert.Contains(published, operation => operation.SchemaVersion == 2 && operation.Kind == CloudFolderSyncOperationKind.Delete);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(10001)]
    public async Task PreviouslyAppliedLongReplacementDoesNotBlockUpgrade(int length)
    {
        var entry = DictionaryEntry("teh", Date(10), UserDataSyncDictionaryEntryType.Correction, new string('x', length));
        var snippet = Snippet(";sig", "Signature", Date(10));
        var oldId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
        var snippetId = UserDataSyncIdentity.SnippetItemId(snippet.Trigger);
        var current = CloudFolderSyncOperation.UpsertDictionary(entry, oldId, "mac", "mac-literal") with { SchemaVersion = 1 };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "mac");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, current.OperationId + ".json"), CloudFolderSyncJson.Serialize(current));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [entry], snippets: [snippet with { Replacement = "Changed", UpdatedAt = Date(20) }]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [oldId, snippetId],
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z", [snippetId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [current.OperationId]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Equal(entry, Assert.Single(store.DictionaryEntries));
        for (var i = 1; i < 3; i++)
            Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30 + i))).OperationsWritten);
        Assert.Contains(OperationFiles("windows").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!), operation => operation.Snippet?.Replacement == "Changed");
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("empty", false)]
    [InlineData("replaced", false)]
    [InlineData("missing", true)]
    [InlineData("empty", true)]
    [InlineData("replaced", true)]
    public async Task UpgradeRebuildsMissingOrReplacedPackage(string package, bool isRegex)
    {
        var entry = DictionaryEntry(isRegex ? @"\d" : "teh", Date(10), UserDataSyncDictionaryEntryType.Correction, "fixed") with { IsRegex = isRegex };
        var snippet = Snippet(";sig", "Signature", Date(10));
        var oldId = UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original);
        var snippetId = UserDataSyncIdentity.SnippetItemId(snippet.Trigger);
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        if (package != "missing") Directory.CreateDirectory(folder);
        if (package == "replaced")
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v2.json"), Path.Combine(folder, "new.json"));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [entry], snippets: [snippet]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [oldId, snippetId],
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z", [snippetId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = ["removed-dictionary", "removed-snippet"]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        var published = OperationFiles("windows").Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Equal(2, published.Length);
        Assert.All(published, operation => Assert.Equal(CloudFolderSyncOperationKind.Upsert, operation.Kind));
        Assert.Equal(isRegex ? 2 : 1, Assert.Single(published, operation => operation.Dictionary is not null).SchemaVersion);
        Assert.Contains(published, operation => operation.Snippet?.Trigger == snippet.Trigger);
        Assert.Contains(entry, store.DictionaryEntries);
        Assert.Equal(package == "replaced" ? 2 : 1, store.DictionaryEntries.Count);
        foreach (var now in new[] { Date(40), Date(50) })
            Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, now)).OperationsWritten);
    }

    [Theory]
    [InlineData("history", false)]
    [InlineData("history", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    public async Task RegexUpgradeIgnoresPrunedOperationsAndUnrelatedFiles(string noise, bool deleted)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "mac");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        File.WriteAllText(Path.Combine(folder, "unrelated.json"), noise == "malformed" ? "{" : """
            {"schemaVersion":1,"operationId":"history-delete","deviceId":"mac","collection":"history",
             "itemId":"history:00000000-0000-0000-0000-000000000001","kind":"delete",
             "updatedAt":"2023-11-14T22:13:25.000Z","deletedAt":"2023-11-14T22:13:25.000Z",
             "historyPayloadVersion":1,"historyGeneration":"history-v1"}
            """);
        var snippet = Snippet(";sig", "Signature", Date(15));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: deleted ? [] : [legacy.Dictionary!], snippets: [snippet]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [legacy.ItemId],
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [legacy.OperationId, "expired-dictionary-delete", "expired-snippet-delete"]
        };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        var published = OperationFiles("windows").Select(path =>
            CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        var dictionaryOperations = published.Where(operation => operation.Collection == UserDataSyncCollection.Dictionary).ToArray();
        if (deleted)
        {
            Assert.Empty(store.DictionaryEntries);
            Assert.Equal(2, dictionaryOperations.Length);
            Assert.All(dictionaryOperations, operation => Assert.Equal(CloudFolderSyncOperationKind.Delete, operation.Kind));
            Assert.Contains(dictionaryOperations, operation => operation.SchemaVersion == 2);
        }
        else
        {
            Assert.Equal(legacy.Dictionary, Assert.Single(store.DictionaryEntries));
            Assert.Equal(2, Assert.Single(dictionaryOperations).SchemaVersion);
            Assert.Equal(CloudFolderSyncOperationKind.Upsert, dictionaryOperations[0].Kind);
        }
        Assert.Contains(published, operation => operation.Snippet?.Trigger == snippet.Trigger && operation.Snippet.Replacement == snippet.Replacement);
        foreach (var now in new[] { Date(40), Date(50) })
            Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, now)).OperationsWritten);
    }

    public static IEnumerable<object[]> IncompleteIgnoredOperationCases()
    {
        foreach (var category in new[] { "history", "future" })
        foreach (var defect in new[] { "minimal", "schemaVersion", "operationId", "deviceId", "collection", "itemId", "kind", "updatedAt", "invalidDate", "blankId", "invalidKind" })
        foreach (var deleted in new[] { false, true })
            yield return [category + ":" + defect, deleted];
        yield return ["history:unknownKind", false];
        yield return ["history:unknownKind", true];
    }

    [Theory]
    [MemberData(nameof(IncompleteIgnoredOperationCases))]
    [InlineData("empty", false)]
    [InlineData("empty", true)]
    [InlineData("dictionary", false)]
    [InlineData("dictionary", true)]
    [InlineData("kind", false)]
    [InlineData("kind", true)]
    [InlineData("deviceId", false)]
    [InlineData("deviceId", true)]
    public async Task StructurallyMalformedHistoryDoesNotTriggerPackageRecovery(string missingField, bool deleted)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var incomplete = System.Text.Json.Nodes.JsonNode.Parse(CloudFolderSyncJson.Serialize(legacy))!.AsObject();
        incomplete["operationId"] = "unapplied-partial";
        var parts = missingField.Split(':');
        if (parts.Length == 2)
        {
            var history = parts[0] == "history";
            incomplete["collection"] = history ? "history" : "dictionary";
            incomplete["schemaVersion"] = history ? 1 : 3;
            switch (parts[1])
            {
                case "minimal":
                    incomplete.Clear();
                    if (history) incomplete["collection"] = "history"; else incomplete["schemaVersion"] = 3;
                    break;
                case "invalidDate": incomplete["updatedAt"] = "not-a-date"; break;
                case "blankId": incomplete["operationId"] = " "; break;
                case "invalidKind": incomplete["kind"] = new System.Text.Json.Nodes.JsonArray(); break;
                case "unknownKind": incomplete["kind"] = "futureKind"; break;
                default: incomplete.Remove(parts[1]); break;
            }
        }
        else if (missingField == "empty") incomplete.Clear();
        else incomplete.Remove(missingField);
        File.WriteAllText(Path.Combine(folder, "partial.json"), incomplete.ToJsonString());
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: deleted ? [] :
            [legacy.Dictionary! with { IsRegex = false, UpdatedAt = Date(20), Replacement = "literal" }]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [legacy.ItemId],
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [legacy.OperationId]
        };
        var previousState = CloudFolderSyncJson.Serialize(state);
        var entitlement = new PaidEntitlements(true);
        await Assert.ThrowsAsync<IOException>(() => CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30)));
        Assert.Equal(previousState, CloudFolderSyncJson.Serialize(state));
        Assert.Equal(deleted ? 0 : 1, store.DictionaryEntries.Count);
        Assert.Empty(OperationFiles("windows"));

        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40));
        var published = OperationFiles("windows").Select(path =>
            CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.Contains(published, operation => operation.SchemaVersion == 2 && operation.Kind == CloudFolderSyncOperationKind.Delete &&
            operation.ItemId == UserDataSyncIdentity.DictionaryItemId(legacy.Dictionary!.EntryType, legacy.Dictionary.Original, true));
        Assert.DoesNotContain(store.DictionaryEntries, entry => entry.IsRegex);
        Assert.DoesNotContain("unapplied-partial", state.AppliedOperationIds);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(50))).OperationsWritten);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("future")]
    [InlineData("future-numeric")]
    public async Task CompleteIgnoredEnvelopesDoNotBlockRecovery(string category)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var envelope = System.Text.Json.Nodes.JsonNode.Parse("""
            {"schemaVersion":1,"operationId":"ignored","deviceId":"peer","collection":"history",
             "itemId":"history:00000000-0000-0000-0000-000000000001","kind":"delete",
             "updatedAt":"2023-11-14T22:13:25.000Z","deletedAt":"2023-11-14T22:13:25.000Z",
             "historyPayloadVersion":1,"historyGeneration":"history-v1"}
            """)!.AsObject();
        if (category != "history")
        {
            envelope["schemaVersion"] = 3;
            envelope["collection"] = category == "future" ? System.Text.Json.Nodes.JsonValue.Create("futureCollection") : System.Text.Json.Nodes.JsonValue.Create(17);
            envelope["kind"] = category == "future" ? System.Text.Json.Nodes.JsonValue.Create("futureKind") : System.Text.Json.Nodes.JsonValue.Create(23);
        }
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ignored.json"), envelope.ToJsonString());
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [legacy.Dictionary!]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [legacy.ItemId],
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [legacy.OperationId]
        };
        var entitlement = new PaidEntitlements(true);
        Assert.Equal(1, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30))).OperationsWritten);
        var published = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(Assert.Single(OperationFiles("windows"))))!;
        Assert.Equal(2, published.SchemaVersion);
        Assert.Equal(CloudFolderSyncOperationKind.Upsert, published.Kind);
        Assert.Equal(legacy.Dictionary, Assert.Single(store.DictionaryEntries));
        Assert.DoesNotContain("ignored", state.AppliedOperationIds);
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RegexUpgradeWaitsForAppliedLegacyFilesBeforeChangingDataOrState(bool deleted, bool missingFile, bool onlyPartial)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "legacy.json");
        if (!missingFile) File.WriteAllText(path, "{");
        var older = CloudFolderSyncOperation.UpsertDictionary(legacy.Dictionary! with
            { IsRegex = false, UpdatedAt = Date(5) }, legacy.ItemId, "old-peer", "older-literal");
        if (!onlyPartial) File.WriteAllText(Path.Combine(folder, "older.json"), CloudFolderSyncJson.Serialize(older));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: deleted ? [] : [legacy.Dictionary!]);
        var state = new CloudFolderSyncState
        {
            DeviceId = "windows", KnownLocalItemIds = [legacy.ItemId],
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" },
            AppliedOperationIds = [legacy.OperationId, older.OperationId]
        };
        var previousState = CloudFolderSyncJson.Serialize(state);
        var entitlement = new PaidEntitlements(true);
        await Assert.ThrowsAsync<IOException>(() => CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(20)));
        Assert.Equal(previousState, CloudFolderSyncJson.Serialize(state));
        Assert.Equal(deleted ? 0 : 1, store.DictionaryEntries.Count);
        Assert.Empty(OperationFiles("windows"));
        File.WriteAllText(path, CloudFolderSyncJson.Serialize(legacy));
        // An unrelated partial file does not block migration once all applied history is readable.
        File.WriteAllText(Path.Combine(folder, "unapplied-partial.json"), "{");
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        var published = OperationFiles("windows").Select(file =>
            CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(file))!).ToArray();
        if (deleted)
        {
            Assert.Empty(store.DictionaryEntries);
            Assert.Contains(published, operation => operation.SchemaVersion == 2 && operation.Kind == CloudFolderSyncOperationKind.Delete);
        }
        else
        {
            Assert.True(Assert.Single(store.DictionaryEntries).IsRegex);
            Assert.Equal(2, Assert.Single(published).SchemaVersion);
            Assert.Equal(CloudFolderSyncOperationKind.Upsert, published[0].Kind);
        }
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
    }

    [Theory]
    [InlineData(1, "syntax")]
    [InlineData(2, "syntax")]
    [InlineData(1, "length")]
    [InlineData(2, "length")]
    [InlineData(1, "newline")]
    [InlineData(2, "newline")]
    [InlineData(1, "carriage-return")]
    [InlineData(2, "carriage-return")]
    public async Task InvalidRegexSyncOperationsDoNotEnterTheDictionary(int schemaVersion, string invalidKind)
    {
        var pattern = invalidKind switch { "length" => new string('a', 161), "newline" => "line\nbreak", "carriage-return" => "line\rbreak", _ => "[" };
        var invalid = DictionaryEntry(pattern, Date(20), UserDataSyncDictionaryEntryType.Correction, "invalid") with { IsRegex = true };
        var operation = CloudFolderSyncOperation.UpsertDictionary(invalid,
            UserDataSyncIdentity.DictionaryItemId(invalid.EntryType, invalid.Original, schemaVersion == 2), "peer") with { SchemaVersion = schemaVersion };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "invalid.json"), CloudFolderSyncJson.Serialize(operation));
        var healthy = DictionaryEntry("healthy", Date(10));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [healthy]);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, new PaidEntitlements(true), Date(30));
        Assert.Equal(healthy, Assert.Single(store.DictionaryEntries));
        Assert.DoesNotContain(operation.OperationId, state.AppliedOperationIds);
    }

    [Theory]
    [InlineData("limit")]
    [InlineData("escaped-newline")]
    public async Task ValidRegexCatalogBoundariesAreSynced(string patternKind)
    {
        var pattern = patternKind == "limit" ? new string('a', 160) : @"\n";
        var entry = DictionaryEntry(pattern, Date(20), UserDataSyncDictionaryEntryType.Correction, "valid") with { IsRegex = true };
        var operation = CloudFolderSyncOperation.UpsertDictionary(entry, UserDataSyncIdentity.DictionaryItemId(entry.EntryType, pattern, true), "peer");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "valid.json"), CloudFolderSyncJson.Serialize(operation));
        var store = new InMemoryUserDataSyncStore();
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, new CloudFolderSyncState { DeviceId = "windows" }, new PaidEntitlements(true), Date(30));
        Assert.Equal(entry, Assert.Single(store.DictionaryEntries));
    }

    [Fact]
    public void MalformedRegexSyncIdentifiersAreSkipped()
    {
        var entry = DictionaryEntry("valid", Date(20), UserDataSyncDictionaryEntryType.Correction, "replacement") with { IsRegex = true };
        var operation = CloudFolderSyncOperation.UpsertDictionary(entry,
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, true), "peer");
        Assert.Empty(CloudFolderSyncEngine.WinningOperations([operation with { ItemId = null! }]));
        Assert.Empty(CloudFolderSyncEngine.WinningOperations([operation with { Dictionary = entry with { Original = null! } }]));
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
            ExportedItemVersions = new() { [oldId] = "2023-11-14T22:13:30.000Z" }
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
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" }
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
            ExportedItemVersions = new() { [legacy.ItemId] = "2023-11-14T22:13:30.000Z" },
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SchemaOneRawRegexPrefixTombstoneNeverDeletesSchemaTwoRegex(bool hasLocalEntries, bool hasLegacyUpsert)
    {
        var regex = DictionaryEntry(@"\d+", Date(20), UserDataSyncDictionaryEntryType.Correction, "regex") with { IsRegex = true };
        var regexId = UserDataSyncIdentity.DictionaryItemId(regex.EntryType, regex.Original, true);
        var literal = DictionaryEntry(regexId["dictionary:correction:".Length..], Date(10), UserDataSyncDictionaryEntryType.Correction, "literal");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        if (hasLegacyUpsert)
            File.WriteAllText(Path.Combine(folder, "literal.json"), CloudFolderSyncJson.Serialize(
                CloudFolderSyncOperation.UpsertDictionary(literal, regexId, "legacy", "raw-literal")));
        File.WriteAllText(Path.Combine(folder, "delete-literal.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, regexId, "legacy", Date(30)) with { SchemaVersion = 1 }));
        File.WriteAllText(Path.Combine(folder, "regex.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.UpsertDictionary(regex, regexId, "modern")));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: hasLocalEntries ? [literal, regex] : []);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(35));
        Assert.Equal(regex, Assert.Single(store.DictionaryEntries));
        Assert.Equal(0, (await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40))).OperationsWritten);
        var newcomer = new InMemoryUserDataSyncStore();
        var newcomerState = new CloudFolderSyncState { DeviceId = "newcomer" };
        await CloudFolderSyncEngine.SyncAsync(_tempDir, newcomer, newcomerState, entitlement, Date(45));
        Assert.Equal(regex, Assert.Single(newcomer.DictionaryEntries));

        File.WriteAllText(Path.Combine(folder, "delete-regex.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, regexId, "modern", Date(50))));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(60));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, newcomer, newcomerState, entitlement, Date(65));
        Assert.Empty(store.DictionaryEntries);
        Assert.Empty(newcomer.DictionaryEntries);
        var afterDeletion = new InMemoryUserDataSyncStore();
        await CloudFolderSyncEngine.SyncAsync(_tempDir, afterDeletion, new() { DeviceId = "after-deletion" }, entitlement, Date(70));
        Assert.Empty(afterDeletion.DictionaryEntries);
    }

    [Theory]
    [InlineData(UserDataSyncDictionaryEntryType.Correction, false)]
    [InlineData(UserDataSyncDictionaryEntryType.Correction, true)]
    [InlineData(UserDataSyncDictionaryEntryType.Term, false)]
    [InlineData(UserDataSyncDictionaryEntryType.Term, true)]
    public async Task SupersededUppercaseLegacyUpsertsStillSupplyDeletionAliases(UserDataSyncDictionaryEntryType type, bool hasLocalEntry)
    {
        var entry = DictionaryEntry("TEH", Date(10), type, type == UserDataSyncDictionaryEntryType.Correction ? "the" : null);
        var rawId = $"dictionary:{(type == UserDataSyncDictionaryEntryType.Correction ? "correction" : "term")}:TEH";
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var upsert = CloudFolderSyncOperation.UpsertDictionary(entry, rawId, "legacy", "uppercase-upsert");
        File.WriteAllText(Path.Combine(folder, "upsert.json"), CloudFolderSyncJson.Serialize(upsert));
        var deletePath = Path.Combine(folder, "delete.json");
        File.WriteAllText(deletePath, CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, rawId, "legacy", Date(20))));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: hasLocalEntry ? [entry] : []);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(30));
        Assert.Empty(store.DictionaryEntries);
        Assert.Contains(upsert.OperationId, state.AppliedOperationIds);
        File.Delete(deletePath);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(40));
        Assert.Empty(store.DictionaryEntries);
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SuppressedLegacyRegexStaysAppliedAfterWinningOperationDisappears(bool deleted, bool unreadable)
    {
        var legacy = CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", "upsert-dictionary-regex-v1.json")))!;
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(legacy));
        var literal = legacy.Dictionary! with { IsRegex = false, UpdatedAt = Date(20), Replacement = "literal" };
        var winner = deleted
            ? CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, legacy.ItemId, "legacy", Date(20))
            : CloudFolderSyncOperation.UpsertDictionary(literal, legacy.ItemId, "legacy");
        var winnerPath = Path.Combine(folder, "winner.json");
        File.WriteAllText(winnerPath, CloudFolderSyncJson.Serialize(winner));
        var store = new InMemoryUserDataSyncStore();
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        if (deleted) Assert.Empty(store.DictionaryEntries);
        else Assert.Equal(literal, Assert.Single(store.DictionaryEntries));
        Assert.Contains(legacy.OperationId, state.AppliedOperationIds);
        // Persist/reload, then lose the operation that suppressed the old regex.
        state = CloudFolderSyncJson.Deserialize<CloudFolderSyncState>(CloudFolderSyncJson.Serialize(state))!;
        if (unreadable) File.WriteAllText(winnerPath, "{");
        else File.Delete(winnerPath);
        foreach (var now in new[] { Date(30), Date(40) })
        {
            var result = await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, now);
            if (deleted) Assert.Empty(store.DictionaryEntries);
            else Assert.Equal(literal, Assert.Single(store.DictionaryEntries));
            Assert.Equal(0, result.OperationsWritten);
        }
    }

    [Fact]
    public async Task StrayTermRegexFlagDoesNotPromoteCorrectionsOrHideLegacyDeletes()
    {
        var term = DictionaryEntry("word", Date(10)) with { IsRegex = true };
        var correction = DictionaryEntry("word", Date(20), UserDataSyncDictionaryEntryType.Correction, "replacement")
            with { IsRegexFieldPresent = false };
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        var upsert = CloudFolderSyncOperation.UpsertDictionary(correction, "dictionary:correction:word", "legacy");
        File.WriteAllText(Path.Combine(folder, "correction.json"), CloudFolderSyncJson.Serialize(upsert));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: [term]);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        var entitlement = new PaidEntitlements(true);
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(25));
        Assert.Equal(term, Assert.Single(store.DictionaryEntries, entry => entry.EntryType == UserDataSyncDictionaryEntryType.Term));
        Assert.False(Assert.Single(store.DictionaryEntries, entry => entry.EntryType == UserDataSyncDictionaryEntryType.Correction).IsRegex);
        var delete = CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary, "dictionary:term:word", "legacy", Date(30));
        File.WriteAllText(Path.Combine(folder, "delete.json"), CloudFolderSyncJson.Serialize(delete));
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, entitlement, Date(35));
        Assert.Equal(UserDataSyncDictionaryEntryType.Correction, Assert.Single(store.DictionaryEntries).EntryType);
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

    [Theory]
    [InlineData(@"\d")]
    [InlineData(" ")]
    public async Task LegacyMissingModePreservesRegexAndRepublishesOnce(string pattern)
    {
        var entry = DictionaryEntry(pattern, Date(10), UserDataSyncDictionaryEntryType.Correction, "#") with { IsRegex = true };
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WhitespaceLegacyUpdateRequiresKnownRegexAndMissingMode(bool hasRegex, bool explicitMode)
    {
        var entry = DictionaryEntry(" ", Date(10), UserDataSyncDictionaryEntryType.Correction, "original") with { IsRegex = true };
        var incoming = entry with { IsRegex = false, IsRegexFieldPresent = explicitMode, Replacement = "rejected", UpdatedAt = Date(20) };
        var operation = CloudFolderSyncOperation.UpsertDictionary(incoming,
            UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original), "legacy");
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(_tempDir), "ops", "legacy");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "legacy.json"), CloudFolderSyncJson.Serialize(operation));
        var store = new InMemoryUserDataSyncStore(dictionaryEntries: hasRegex ? [entry] : []);
        var state = new CloudFolderSyncState { DeviceId = "windows" };
        await CloudFolderSyncEngine.SyncAsync(_tempDir, store, state, new PaidEntitlements(true), Date(30));
        if (hasRegex) Assert.Equal(entry, Assert.Single(store.DictionaryEntries));
        else Assert.Empty(store.DictionaryEntries);
        Assert.DoesNotContain(operation.OperationId, state.AppliedOperationIds);
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
