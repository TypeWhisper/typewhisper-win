using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

public sealed class DictationSnippetSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-snippets-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "snippets.json");
    public DictationSnippetSnapshotTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);
    private PrototypeLexicon Open() => new(snippetPath: FilePath);

    [Fact]
    public void SaveEditDisableAndDeleteSurviveRestart()
    {
        var store = Open();
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "my signature", "Viele Grüße\nMarco", "email", true)));
        store = Open();
        var saved = Assert.Single(store.Entries);
        Assert.Equal("Viele Grüße\nMarco", saved.Value);
        Assert.Equal("email", saved.Tags);
        Assert.True(saved.CaseSensitive);
        Assert.Null(store.Save(saved with { Value = "Grüße", Enabled = false }));
        store = Open();
        Assert.False(Assert.Single(store.Entries).Enabled);
        Assert.Equal("Grüße", Assert.Single(store.Entries).Value);
        Assert.True(store.Remove(saved.Id));
        Assert.Empty(Open().Entries);
    }

    [Fact]
    public void RecordingSnapshotDoesNotOverwriteNewerEditsOrUsageMetadata()
    {
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new[] { new Snippet
        { Id = "opaque-existing-id", Trigger = "signature", Replacement = "Old", UsageCount = 12 } }));
        var snapshot = DictationSnippetSnapshot.Load(FilePath);
        var store = Open();
        Assert.Null(store.Save(Assert.Single(store.Entries) with { Value = "New" }));
        var bytes = File.ReadAllBytes(FilePath);
        Assert.Equal("Old", snapshot.Apply("signature.").Text);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal("New", DictationSnippetSnapshot.Load(FilePath).Apply("signature").Text);
        var persisted = Assert.Single(DictationSnippetSnapshot.ReadEntries(FilePath));
        Assert.Equal("opaque-existing-id", persisted.Id);
        Assert.Equal(12, persisted.UsageCount);
    }

    [Fact]
    public void ExpansionUsesExistingCasePunctuationMultilineAndPlaceholderRules()
    {
        var store = Open();
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "my signature", "Grüße\nMarco $1")));
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "today", "{date:yyyy-MM-dd}")));
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "URL", "https://example.com", CaseSensitive: true)));
        Assert.Null(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "disabled", "Hidden", Enabled: false)));
        var snapshot = DictationSnippetSnapshot.Load(FilePath);
        Assert.Equal("Grüße\nMarco $1", snapshot.Apply("MY SIGNATURE!").Text);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), snapshot.Apply("today").Text);
        Assert.Equal("url disabled", snapshot.Apply("url disabled").Text);
        Assert.Equal("https://example.com", snapshot.Apply("URL").Text);
    }

    [Fact]
    public void ClipboardIsRequestedOnlyForMatchingEnabledSnippets()
    {
        var store = Open();
        store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "link", "See {clipboard}"));
        store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "disabled", "{clipboard}", Enabled: false));
        var snapshot = DictationSnippetSnapshot.Load(FilePath);
        Assert.False(snapshot.NeedsClipboard("normal disabled"));
        Assert.True(snapshot.NeedsClipboard("link"));
        var reads = 0;
        Assert.Equal("normal", snapshot.Apply("normal", () => { reads++; return "secret"; }).Text);
        Assert.Equal(0, reads);
        Assert.Equal("See https://example.com", snapshot.Apply("link", () => "https://example.com").Text);
        var failed = snapshot.Apply("link");
        Assert.Equal("link", failed.Text);
        Assert.NotNull(failed.Error);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"x\",\"Trigger\":\"\",\"Replacement\":\"oops\"}]")]
    public void MalformedStorageCannotBeOverwrittenAndRetainsTranscript(string json)
    {
        File.WriteAllText(FilePath, json);
        var store = Open();
        Assert.NotNull(store.LastError);
        Assert.NotNull(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "hello", "Changed")));
        var result = DictationSnippetSnapshot.Load(FilePath).Apply("hello");
        Assert.NotNull(result.Error);
        Assert.Equal("hello", result.Text);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void FailedWriteKeepsPreviousEditorState()
    {
        var store = Open();
        Directory.CreateDirectory(FilePath);
        Assert.NotNull(store.Save(new(Guid.NewGuid(), PrototypeLexiconKind.Snippet, "hello", "Changed")));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void MissingStorageDoesNotCreateFileOrChangeText()
    {
        Assert.Empty(Open().Entries);
        Assert.Equal("hello", DictationSnippetSnapshot.Load(FilePath).Apply("hello").Text);
        Assert.False(File.Exists(FilePath));
    }
}
