using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.Sync;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Entries received through the sync folder must leave catalogs the Dictionary and Snippets pages can open,
// and entries that could change or submit dictated text must not run before they are reviewed.
public sealed class CloudSyncLexiconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cloud-sync-lexicon-" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "profile");
    private string Folder => Path.Combine(_root, "cloud");
    private static readonly DateTime At = DateTime.UtcNow.AddHours(-1);

    public CloudSyncLexiconTests()
    {
        Directory.CreateDirectory(Profile);
        Directory.CreateDirectory(Folder);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void Write(CloudFolderSyncOperation operation)
    {
        var folder = Path.Combine(CloudFolderSyncEngine.PackagePath(Folder), "ops", "peer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, operation.OperationId + ".json"), CloudFolderSyncJson.Serialize(operation));
    }

    private static UserDataSyncDictionaryEntry Entry(UserDataSyncDictionaryEntryType type, string original, string? replacement, bool regex = false) =>
        new(type, original, replacement, false, true, At, At, IsRegex: regex);

    private void WriteEntry(UserDataSyncDictionaryEntry entry) =>
        Write(CloudFolderSyncOperation.UpsertDictionary(entry, UserDataSyncIdentity.DictionaryItemId(entry.EntryType, entry.Original, entry.IsRegex), "peer"));

    private void WriteSnippet(string trigger, string replacement, IReadOnlyList<string>? tags = null) =>
        Write(CloudFolderSyncOperation.UpsertSnippet(new UserDataSyncSnippet(trigger, replacement, false, true, tags ?? [], At, At),
            UserDataSyncIdentity.SnippetItemId(trigger), "peer"));

    [Fact]
    public async Task ReceivedEntriesLeaveCatalogsTheLexiconPagesOpenAndWaitForReview()
    {
        // Entries the local catalogs refuse.
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Term, new string('a', 161), null));
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Term, " padded ", "not for terms"));
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Correction, "two\nlines", "x"));
        WriteSnippet(";long", new string('x', 10_001));
        WriteSnippet(";tags", "x", [new string('t', 301)]);
        // Entries that can rewrite or submit dictated text, or insert the clipboard.
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Correction, "(?s).+", "replaced", regex: true));
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Correction, "hello", "hello\\nworld"));
        WriteSnippet("the", "{clipboard}");
        // Ordinary entries.
        WriteEntry(Entry(UserDataSyncDictionaryEntryType.Correction, "teh", "the"));
        WriteSnippet(";hi", "Hello");

        var sync = new PersistedCloudFolderSync(Profile);
        sync.Configure(Folder, true);
        var result = await sync.SyncAsync(() => true);
        Assert.Equal(3, result.HeldForReview);

        var dictionary = LexiconTransfer.ReadDictionary(File.ReadAllText(Path.Combine(Profile, "dictionary.json")), allowPackEntries: true);
        var snippets = LexiconTransfer.ReadSnippets(File.ReadAllText(Path.Combine(Profile, "snippets.json")));
        Assert.Equal(["(?s).+", "hello", "padded", "teh"], dictionary.Select(entry => entry.Original).Order(StringComparer.Ordinal));
        Assert.Null(dictionary.Single(entry => entry.Original == "padded").Replacement);
        Assert.Equal(["padded", "teh"], dictionary.Where(entry => entry.IsEnabled).Select(entry => entry.Original).Order(StringComparer.Ordinal));
        Assert.Equal([";hi", "the"], snippets.Select(snippet => snippet.Trigger).Order(StringComparer.Ordinal));
        Assert.False(snippets.Single(snippet => snippet.Trigger == "the").IsEnabled);

        // Dictation applies the ordinary entries only, and never reads the clipboard for the waiting snippet.
        var snapshot = DictationLexiconSnapshot.Load(Path.Combine(Profile, "dictionary.json"), Path.Combine(Profile, "snippets.json"));
        var processed = await snapshot.ProcessAsync("hello teh the", new() { TranscriptionNumberNormalizationEnabled = false }, "en", "en", false,
            _ => throw new InvalidOperationException("Clipboard must not be read."), default, TranscriptionTask.Transcribe, null, "test-provider", "test-model");
        Assert.Equal("hello the the", processed.Text);
    }
}
