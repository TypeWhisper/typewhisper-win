using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class CatalogSnapshotCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-snapshot-cache-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "catalog.json");
    private int _builds;
    public CatalogSnapshotCacheTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    // Every build returns a new string instance, so reference equality shows whether a build was reused.
    private CatalogSnapshotCache<string> Cache() => new(Build, text => !text.StartsWith("{broken", StringComparison.Ordinal));
    private string Build(string path)
    {
        _builds++;
        return File.Exists(path) ? File.ReadAllText(path) : "missing " + _builds;
    }

    private void Write(string text, DateTime? lastWriteUtc = null)
    {
        File.WriteAllText(FilePath, text);
        if (lastWriteUtc is { } stamp) File.SetLastWriteTimeUtc(FilePath, stamp);
    }

    [Fact]
    public void UnchangedFileReturnsTheSameSnapshot()
    {
        Write("version 1");
        var cache = Cache();
        var first = cache.Get(FilePath);
        Assert.Same(first, cache.Get(FilePath));
        Assert.Equal("version 1", first);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public void ChangeNotificationRebuildsEvenWhenTheStampCannotTell()
    {
        Write("version 1");
        var cache = Cache();
        var first = cache.Get(FilePath);
        // Same length and the previous write time: the rewrite a stamp cannot see, which is why in-process writers announce it.
        Write("version 2", File.GetLastWriteTimeUtc(FilePath));
        Assert.Same(first, cache.Get(FilePath));
        cache.Invalidate();
        var rebuilt = cache.Get(FilePath);
        Assert.NotSame(first, rebuilt);
        Assert.Equal("version 2", rebuilt);
        Assert.Equal("version 1", first);
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void ChangedFileStampRebuildsWithoutNotification()
    {
        Write("version 1");
        var cache = Cache();
        var first = cache.Get(FilePath);
        // Another process or the cloud sync wrote the file: only the stamp reveals it.
        Write("version 2 of the catalog", File.GetLastWriteTimeUtc(FilePath).AddSeconds(1));
        var rebuilt = cache.Get(FilePath);
        Assert.NotSame(first, rebuilt);
        Assert.Equal("version 2 of the catalog", rebuilt);
        Assert.Same(rebuilt, cache.Get(FilePath));
        File.Delete(FilePath);
        Assert.Equal("missing 3", cache.Get(FilePath));
        Assert.Equal("missing 3", cache.Get(FilePath));
        Assert.Equal(3, _builds);
    }

    [Fact]
    public void DifferentPathIsBuiltSeparately()
    {
        Write("first catalog");
        var other = Path.Combine(_directory, "other.json");
        File.WriteAllText(other, "other catalog");
        var cache = Cache();
        var first = cache.Get(FilePath);
        Assert.Equal("other catalog", cache.Get(other));
        Assert.Equal("first catalog", cache.Get(FilePath));
        Assert.NotSame(first, cache.Get(FilePath));
        Assert.Equal(3, _builds);
    }

    [Fact]
    public void UnreadableCatalogIsNotReused()
    {
        Write("{broken");
        var cache = Cache();
        var first = cache.Get(FilePath);
        // A sync client releasing a locked file leaves the stamp unchanged, so a failed load must be retried.
        Assert.NotSame(first, cache.Get(FilePath));
        Assert.Equal(2, _builds);
        Write("repaired", File.GetLastWriteTimeUtc(FilePath).AddSeconds(1));
        var repaired = cache.Get(FilePath);
        Assert.Equal("repaired", repaired);
        Assert.Same(repaired, cache.Get(FilePath));
        Assert.Equal(3, _builds);
    }

    [Fact]
    public void NotificationDuringBuildDiscardsThatBuild()
    {
        Write("version 1");
        CatalogSnapshotCache<string>? cache = null;
        cache = new(path =>
        {
            var built = Build(path);
            // A writer that saved while this build was reading must win over the snapshot being built.
            if (_builds == 1) cache!.Invalidate();
            return built;
        }, _ => true);
        var first = cache.Get(FilePath);
        var second = cache.Get(FilePath);
        Assert.NotSame(first, second);
        Assert.Same(second, cache.Get(FilePath));
        Assert.Equal(2, _builds);
    }

    [Fact]
    public void UnstampablePathIsBuiltEveryTime()
    {
        var cache = Cache();
        Assert.Equal("missing 1", cache.Get(""));
        Assert.Equal("missing 2", cache.Get(""));
    }
}

// The recording snapshots share one process-wide cache, so these tests must not overlap with other catalog tests.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedRecordingSnapshotCollection
{
    public const string Name = "Shared recording snapshot";
}

[Collection(SharedRecordingSnapshotCollection.Name)]
public sealed class SharedRecordingSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-shared-snapshot-" + Guid.NewGuid().ToString("N"));
    private string DictionaryPath => Path.Combine(_directory, "dictionary.json");
    private string SnippetPath => Path.Combine(_directory, "snippets.json");
    public SharedRecordingSnapshotTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void DictationsShareOneSnapshotUntilAnEditIsSaved()
    {
        var store = new Lexicon(DictionaryPath, SnippetPath);
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Correction, "teh", "the")));
        Assert.Null(store.Save(new(Guid.NewGuid(), LexiconKind.Snippet, "sig", "Regards")));
        var dictionary = DictationDictionarySnapshot.Load(DictionaryPath);
        var snippets = DictationSnippetSnapshot.Load(SnippetPath);
        Assert.Same(dictionary, DictationDictionarySnapshot.Load(DictionaryPath));
        Assert.Same(snippets, DictationSnippetSnapshot.Load(SnippetPath));
        Assert.Null(store.Save(store.Entries.Single(e => e.Kind == LexiconKind.Correction) with { Value = "thy" }));
        Assert.Null(store.Save(store.Entries.Single(e => e.Kind == LexiconKind.Snippet) with { Value = "Cheers" }));
        var updatedDictionary = DictationDictionarySnapshot.Load(DictionaryPath);
        var updatedSnippets = DictationSnippetSnapshot.Load(SnippetPath);
        Assert.NotSame(dictionary, updatedDictionary);
        Assert.NotSame(snippets, updatedSnippets);
        Assert.Equal("thy", updatedDictionary.ApplyCorrections("teh"));
        Assert.Equal("Cheers", updatedSnippets.Apply("sig").Text);
        // A recording that started before the edit keeps the catalog it started with.
        Assert.Equal("the", dictionary.ApplyCorrections("teh"));
        Assert.Equal("Regards", snippets.Apply("sig").Text);
    }

    [Fact]
    public void FileTranscriptionReusesTheDictationSnapshot()
    {
        File.WriteAllText(DictionaryPath, JsonSerializer.Serialize(new[]
        { new DictionaryEntry { Id = "fix", EntryType = DictionaryEntryType.Correction, Original = "teh", Replacement = "the" } }));
        var dictionary = DictationDictionarySnapshot.Load(DictionaryPath);
        Assert.Same(dictionary, DictationLexiconSnapshot.Load(DictionaryPath, SnippetPath).Dictionary);
    }
}
