using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;

namespace TypeWhisper.Core.Tests.Services;

public sealed class PersistedCloudFolderSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-sync-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "cloud");
    private string Profile(string name) => Path.Combine(_root, name);
    public PersistedCloudFolderSyncTests() => Directory.CreateDirectory(Folder);
    public void Dispose() { Directory.Delete(_root, true); }
    private PersistedCloudFolderSync Client(string name)
    {
        Directory.CreateDirectory(Profile(name));
        var client = new PersistedCloudFolderSync(Profile(name));
        client.Configure(Folder, true); return client;
    }
    private void Write<T>(string device, string name, T value) => File.WriteAllText(Path.Combine(Profile(device), name), JsonSerializer.Serialize(value));
    private T Read<T>(string device, string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Profile(device), name)))!;
    private static DictionaryEntry Word(string name, DateTime time) => new() { Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Term, Original = name, CreatedAt = time, UpdatedAt = time, CtcMinSimilarity = .65f };

    [Fact]
    public async Task TermRegexFlagsAreClearedAcrossPersistedSyncAndIncomingUpdates()
    {
        var a = Client("a"); var b = Client("b");
        var time = DateTime.UtcNow.AddHours(-1);
        var term = Word("[", time) with { IsRegex = true };
        Write("a", "dictionary.json", new[] { term });
        await a.SyncAsync(() => true);
        Assert.False(Assert.Single(Read<DictionaryEntry[]>("a", "dictionary.json")).IsRegex);
        var operations = Directory.GetFiles(Path.Combine(CloudFolderSyncEngine.PackagePath(Folder), "ops"), "*.json", SearchOption.AllDirectories)
            .Select(path => CloudFolderSyncJson.Deserialize<CloudFolderSyncOperation>(File.ReadAllText(path))!).ToArray();
        Assert.False(Assert.Single(operations).Dictionary!.IsRegex);
        await b.SyncAsync(() => true);
        Assert.False(Assert.Single(Read<DictionaryEntry[]>("b", "dictionary.json")).IsRegex);

        var peerFolder = Path.Combine(CloudFolderSyncEngine.PackagePath(Folder), "ops", "legacy-peer");
        Directory.CreateDirectory(peerFolder);
        var incoming = new UserDataSyncDictionaryEntry(UserDataSyncDictionaryEntryType.Term, "[", null,
            false, true, time, time.AddMinutes(1), IsRegex: true);
        foreach (var original in new[] { "[", "new term" })
            File.WriteAllText(Path.Combine(peerFolder, original == "[" ? "update.json" : "insert.json"),
                CloudFolderSyncJson.Serialize(CloudFolderSyncOperation.UpsertDictionary(incoming with { Original = original },
                    UserDataSyncIdentity.DictionaryItemId(incoming.EntryType, original), "legacy-peer")));
        await b.SyncAsync(() => true);
        var received = Read<DictionaryEntry[]>("b", "dictionary.json");
        Assert.Equal(2, received.Length);
        Assert.All(received, entry => Assert.False(entry.IsRegex));

        Write("b", "dictionary.json", received.Select(entry => entry with { IsRegex = true }).ToArray());
        var missingMode = incoming with { UpdatedAt = time.AddMinutes(2), IsRegexFieldPresent = false };
        File.WriteAllText(Path.Combine(peerFolder, "missing-mode.json"), CloudFolderSyncJson.Serialize(
            CloudFolderSyncOperation.UpsertDictionary(missingMode, UserDataSyncIdentity.DictionaryItemId(missingMode.EntryType, missingMode.Original), "legacy-peer")));
        await b.SyncAsync(() => true);
        Assert.All(Read<DictionaryEntry[]>("b", "dictionary.json"), entry => Assert.False(entry.IsRegex));
        var repeated = await new PersistedCloudFolderSync(Profile("b")).SyncAsync(() => true);
        Assert.Equal(0, repeated.OperationsWritten);
        Assert.Equal(0, repeated.MutationsApplied);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedSyncAllowsWhitespaceOriginalOnlyForRegexCorrections(bool correction)
    {
        var a = Client("a");
        var entry = Word(" ", DateTime.UtcNow.AddHours(-1)) with { IsRegex = true,
            EntryType = correction ? DictionaryEntryType.Correction : DictionaryEntryType.Term, Replacement = "-" };
        Write("a", "dictionary.json", new[] { entry });
        if (!correction)
        {
            await Assert.ThrowsAsync<JsonException>(() => a.SyncAsync(() => true));
            Assert.False(Directory.Exists(CloudFolderSyncEngine.PackagePath(Folder)));
            Assert.Null(a.Preferences.State!.LastSyncAt);
            return;
        }
        await a.SyncAsync(() => true);
        var b = Client("b");
        await b.SyncAsync(() => true);
        var received = Assert.Single(Read<DictionaryEntry[]>("b", "dictionary.json"));
        Assert.Equal(" ", received.Original);
        Assert.True(received.IsRegex);
    }

    [Fact]
    public async Task RegexIdentitiesSurvivePersistedSyncAndRestart()
    {
        var a = Client("a"); var b = Client("b");
        var time = DateTime.UtcNow.AddHours(-1);
        var digit = Word(@"\d", time) with { EntryType = DictionaryEntryType.Correction, Replacement = "#", IsRegex = true, CtcMinSimilarity = null };
        var other = digit with { Id = Guid.NewGuid().ToString(), Original = @"\D" };
        var literal = digit with { Id = Guid.NewGuid().ToString(), IsRegex = false };
        Write("a", "dictionary.json", new[] { digit, other, literal });
        await a.SyncAsync(() => true); await b.SyncAsync(() => true);
        var received = Read<DictionaryEntry[]>("b", "dictionary.json");
        Assert.Equal(3, received.Length);
        Write("b", "dictionary.json", received.Where(entry => !(entry.IsRegex && entry.Original == @"\D")).ToArray());
        await b.SyncAsync(() => true); await a.SyncAsync(() => true);
        var remaining = Read<DictionaryEntry[]>("a", "dictionary.json");
        Assert.Equal(2, remaining.Length);
        Assert.Single(remaining, entry => entry.IsRegex);
        var restarted = new PersistedCloudFolderSync(Profile("a"));
        var repeated = await restarted.SyncAsync(() => true);
        Assert.Equal(0, repeated.OperationsWritten);
        Assert.Equal(0, repeated.MutationsApplied);
    }

    [Fact]
    public async Task TwoDevicesExchangeEditsAndDeletesAndKeepProgressAfterRestart()
    {
        var a = Client("a"); var b = Client("b");
        var time = DateTime.UtcNow.AddHours(-1);
        Write("a", "dictionary.json", new[] { Word("TypeWhisper", time), Word("packword", time) with { Id = "pack:sample:word" } });
        Write("a", "snippets.json", new[] { new Snippet { Id = "local-id", Trigger = ";sig", Replacement = "Original", Tags = "work", CreatedAt = time, UpdatedAt = time } });
        await a.SyncAsync(() => true); await b.SyncAsync(() => true);
        var word = Assert.Single(Read<DictionaryEntry[]>("b", "dictionary.json"));
        Assert.Equal(.65f, word.CtcMinSimilarity);
        var snippet = Assert.Single(Read<Snippet[]>("b", "snippets.json"));
        Assert.Equal("work", snippet.Tags);
        Write("b", "snippets.json", new[] { snippet with { Replacement = "Edited on B", UpdatedAt = DateTime.UtcNow } });
        Write("b", "dictionary.json", Array.Empty<DictionaryEntry>());
        await b.SyncAsync(() => true); await a.SyncAsync(() => true);
        Assert.Equal("Edited on B", Assert.Single(Read<Snippet[]>("a", "snippets.json")).Replacement);
        Assert.StartsWith("pack:", Assert.Single(Read<DictionaryEntry[]>("a", "dictionary.json")).Id);
        var restarted = new PersistedCloudFolderSync(Profile("a"));
        Assert.True(restarted.Preferences.Enabled);
        Assert.Equal(a.Preferences.State!.DeviceId, restarted.Preferences.State!.DeviceId);
        var again = await restarted.SyncAsync(() => true);
        Assert.Equal(0, again.OperationsWritten); Assert.Equal(0, again.MutationsApplied);
        var c = Client("c"); await c.SyncAsync(() => true);
        Assert.False(File.Exists(Path.Combine(Profile("c"), "dictionary.json")));
    }

    [Fact]
    public async Task MacFixturePreservesTuningAndLegacyCorrectionIdentity()
    {
        var client = Client("windows");
        var ops = Path.Combine(CloudFolderSyncEngine.PackagePath(Folder), "ops", "fixture-mac");
        Directory.CreateDirectory(ops);
        foreach (var name in new[] { "upsert-dictionary-ctc-v1.json", "upsert-dictionary-v1.json", "upsert-snippet-legacy-v1.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PremiumSync", name), Path.Combine(ops, name));
        await client.SyncAsync(() => true);
        var words = Read<DictionaryEntry[]>("windows", "dictionary.json");
        Assert.Equal(.65f, words.Single(e => e.Original == "TypeWhisper").CtcMinSimilarity);
        Assert.Equal(DictionaryEntrySource.AutoLearned, words.Single(e => e.Original == "recieve").Source);
        var deletion = CloudFolderSyncOperation.Delete(UserDataSyncCollection.Dictionary,
            "dictionary:correction:recieve", "fixture-mac", DateTime.UtcNow);
        File.WriteAllText(Path.Combine(ops, "delete.json"), CloudFolderSyncJson.Serialize(deletion));
        await client.SyncAsync(() => true);
        Assert.DoesNotContain(Read<DictionaryEntry[]>("windows", "dictionary.json"), e => e.Original == "recieve");
    }

    [Fact]
    public async Task ConcurrentLocalEditIsKeptAndProgressDoesNotAdvance()
    {
        var client = Client("a");
        Write("a", "dictionary.json", new[] { Word("Before", DateTime.UtcNow) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync(() => true, (commit, changed) =>
        {
            Write("a", "dictionary.json", new[] { Word("During sync", DateTime.UtcNow) });
            commit(); return Task.CompletedTask;
        }));
        Assert.Equal("During sync", Assert.Single(Read<DictionaryEntry[]>("a", "dictionary.json")).Original);
        Assert.Null(client.Preferences.State!.LastSyncAt);
    }

    [Fact]
    public async Task RevokedAccessBeforeCommitDoesNotAdvanceState()
    {
        var client = Client("a"); var entitled = true;
        await Assert.ThrowsAsync<CloudFolderSyncNotEntitledException>(() => client.SyncAsync(() => entitled, (commit, changed) =>
        { entitled = false; commit(); return Task.CompletedTask; }));
        Assert.Null(client.Preferences.State!.LastSyncAt);
    }

    [Fact]
    public async Task CancellationBeforeCommitDoesNotAdvanceState()
    {
        var client = Client("a"); using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SyncAsync(() => true, (commit, changed) =>
        { cancellation.Cancel(); commit(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Null(client.Preferences.State!.LastSyncAt);
    }

    [Fact]
    public async Task MalformedCatalogIsNotPublishedAsEmpty()
    {
        var client = Client("a"); File.WriteAllText(Path.Combine(Profile("a"), "dictionary.json"), "broken");
        await Assert.ThrowsAsync<JsonException>(() => client.SyncAsync(() => true));
        Assert.False(Directory.Exists(CloudFolderSyncEngine.PackagePath(Folder)));
    }

    [Fact]
    public async Task MissingCatalogCannotDeleteRemoteData()
    {
        var client = Client("a"); Write("a", "dictionary.json", new[] { Word("Keep", DateTime.UtcNow) });
        await client.SyncAsync(() => true);
        File.Delete(Path.Combine(Profile("a"), "dictionary.json"));
        await Assert.ThrowsAsync<IOException>(() => client.SyncAsync(() => true));
        var other = Client("b"); await other.SyncAsync(() => true);
        Assert.Equal("Keep", Assert.Single(Read<DictionaryEntry[]>("b", "dictionary.json")).Original);
    }

    [Fact]
    public async Task PausingKeepsDataAndUnentitledSyncWritesNothing()
    {
        var client = Client("a");
        await Assert.ThrowsAsync<CloudFolderSyncNotEntitledException>(() => client.SyncAsync(() => false));
        Assert.False(Directory.Exists(CloudFolderSyncEngine.PackagePath(Folder)));
        client.Configure(Folder, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync(() => true));
        Assert.False(new PersistedCloudFolderSync(Profile("a")).Preferences.Enabled);
    }

    [Fact]
    public async Task FolderCannotChangeDuringCommit()
    {
        var client = Client("a");
        await client.SyncAsync(() => true, (commit, changed) =>
        { Assert.Throws<InvalidOperationException>(() => client.Configure(null, false)); commit(); return Task.CompletedTask; });
        Assert.Equal(Folder, client.Preferences.Folder);
    }

    [Fact]
    public async Task UnchangedPassLeavesPreferencesFileAlone()
    {
        var client = Client("a");
        Write("a", "dictionary.json", new[] { Word("Keep", DateTime.UtcNow) });
        await client.SyncAsync(() => true);
        var preferencesPath = Path.Combine(Profile("a"), "cloud-folder-sync.json");
        var stale = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(preferencesPath, stale);
        var before = client.Preferences; var bytes = File.ReadAllBytes(preferencesPath);
        var published = false;
        var result = await client.SyncAsync(() => true, (commit, changed) => { published = true; Assert.False(changed); commit(); return Task.CompletedTask; });
        Assert.True(published); Assert.Equal(0, result.MutationsApplied);
        Assert.Equal(stale, File.GetLastWriteTimeUtc(preferencesPath));
        Assert.Equal(bytes, File.ReadAllBytes(preferencesPath));
        Assert.Same(before, client.Preferences);
    }

    [Fact]
    public async Task PassThatAppliesRemoteChangesStillSavesProgress()
    {
        var a = Client("a"); Write("a", "dictionary.json", new[] { Word("Shared", DateTime.UtcNow) });
        await a.SyncAsync(() => true);
        var b = Client("b");
        var preferencesPath = Path.Combine(Profile("b"), "cloud-folder-sync.json");
        var stale = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(preferencesPath, stale);
        var result = await b.SyncAsync(() => true);
        Assert.Equal(1, result.MutationsApplied);
        Assert.NotEqual(stale, File.GetLastWriteTimeUtc(preferencesPath));
        Assert.Equal("Shared", Assert.Single(Read<DictionaryEntry[]>("b", "dictionary.json")).Original);
        Assert.Contains(new PersistedCloudFolderSync(Profile("b")).Preferences.State!.KnownLocalItemIds, id => id.StartsWith("dictionary:", StringComparison.Ordinal));
    }
}
