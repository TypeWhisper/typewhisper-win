using System.Text.Json;
using System.Text.Json.Nodes;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

public sealed class SnippetUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-snippet-usage-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_directory, "snippets.json");
    private static Snippet Entry(string id, string trigger, string replacement) => new()
    {
        Id = id, Trigger = trigger, Replacement = replacement,
        CreatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)
    };
    private void Save(params Snippet[] entries)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(CatalogPath, JsonSerializer.Serialize(entries));
    }
    private Snippet[] Read() => JsonSerializer.Deserialize<Snippet[]>(File.ReadAllText(CatalogPath))!;

    [Fact]
    public void ClipboardProbeDoesNotCountAndActualExpansionReportsEachAppliedIdOnce()
    {
        Save(Entry("clipboard", "my clip", "{clipboard}"));
        var snapshot = DictationSnippetSnapshot.Load(CatalogPath);
        Assert.True(snapshot.NeedsClipboard("my clip my clip"));
        Assert.Equal(0, Assert.Single(Read()).UsageCount);
        var expanded = snapshot.ApplyWithUsage("my clip my clip", () => "pasted");
        Assert.Equal("pasted pasted", expanded.Text);
        Assert.Equal(new[] { "clipboard" }, expanded.AppliedIds);
        Assert.Equal(1, SnippetUsageRecorder.Record(CatalogPath, expanded.AppliedIds));
        Assert.Equal(1, Assert.Single(Read()).UsageCount);
    }

    [Fact]
    public void FailedExpansionDiscardsEarlierAppliedIdsAndDoesNotCountPartialWork()
    {
        Save(Entry("first", "long successful phrase", "success"), Entry("second", "clip", "{clipboard}"));
        var expanded = DictationSnippetSnapshot.Load(CatalogPath).ApplyWithUsage("long successful phrase clip");
        Assert.NotNull(expanded.Error);
        Assert.Equal("long successful phrase clip", expanded.Text);
        Assert.Empty(expanded.AppliedIds);
        Assert.Equal(0, SnippetUsageRecorder.Record(CatalogPath, expanded.AppliedIds));
        Assert.All(Read(), snippet => Assert.Equal(0, snippet.UsageCount));
    }

    [Fact]
    public void UsageMergesIntoCurrentEditsAndDoesNotResurrectDeletedSnapshotEntries()
    {
        Save(Entry("kept", "first", "old"), Entry("deleted", "second", "removed"));
        var expanded = DictationSnippetSnapshot.Load(CatalogPath).ApplyWithUsage("first second");
        Save(Entry("kept", "renamed", "new content") with { Tags = "new tag", UsageCount = 8 });
        Assert.Equal(1, SnippetUsageRecorder.Record(CatalogPath, expanded.AppliedIds.Concat(expanded.AppliedIds).ToArray()));
        var kept = Assert.Single(Read());
        Assert.Equal("renamed", kept.Trigger); Assert.Equal("new content", kept.Replacement);
        Assert.Equal("new tag", kept.Tags); Assert.Equal(9, kept.UsageCount);
    }

    [Fact]
    public void UsageRetainsUnknownMetadataAndExistingTimestamps()
    {
        Save(Entry("one", "trigger", "replacement"));
        var nodes = JsonNode.Parse(File.ReadAllText(CatalogPath))!.AsArray();
        nodes[0]!["FutureMetadata"] = new JsonObject { ["value"] = "keep me" };
        File.WriteAllText(CatalogPath, nodes.ToJsonString());
        var before = JsonNode.Parse(File.ReadAllText(CatalogPath))!.AsArray()[0]!;
        SnippetUsageRecorder.Record(CatalogPath, ["one"]);
        var after = JsonNode.Parse(File.ReadAllText(CatalogPath))!.AsArray()[0]!;
        Assert.Equal("keep me", after["FutureMetadata"]!["value"]!.GetValue<string>());
        Assert.Equal(before["UpdatedAt"]!.ToJsonString(), after["UpdatedAt"]!.ToJsonString());
        Assert.Equal(before["CreatedAt"]!.ToJsonString(), after["CreatedAt"]!.ToJsonString());
    }

    [Fact]
    public void CorruptLaterEntryPreventsAllCounterChanges()
    {
        Directory.CreateDirectory(_directory);
        const string json = """[{"Id":"one","UsageCount":1},{"Id":"two","UsageCount":-1}]""";
        File.WriteAllText(CatalogPath, json);
        Assert.Throws<JsonException>(() => SnippetUsageRecorder.Record(CatalogPath, ["one"]));
        Assert.Equal(json, File.ReadAllText(CatalogPath));
    }

    [Fact]
    public void OverflowKeepsCurrentCatalogUnchanged()
    {
        Save(Entry("one", "trigger", "replacement") with { UsageCount = int.MaxValue });
        var original = File.ReadAllText(CatalogPath);
        Assert.Throws<OverflowException>(() => SnippetUsageRecorder.Record(CatalogPath, ["one"]));
        Assert.Equal(original, File.ReadAllText(CatalogPath));
    }

    [Fact]
    public async Task ConcurrentSuccessfulExpansionsDoNotLoseUsageIncrements()
    {
        Save(Entry("one", "trigger", "replacement"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => SnippetUsageRecorder.Record(CatalogPath, ["one"]))));
        Assert.Equal(12, Assert.Single(Read()).UsageCount);
    }

    [Fact]
    public void EditingThroughTransactionUsesLatestCountersAndPreservesFailedWrites()
    {
        Save(Entry("one", "trigger", "original"));
        SnippetUsageRecorder.Record(CatalogPath, ["one"]);
        SnippetCatalogTransaction.Update(CatalogPath, current => current.Select(entry => entry with { Replacement = "edited" }).ToArray());
        var edited = Assert.Single(Read());
        Assert.Equal(1, edited.UsageCount); Assert.Equal("edited", edited.Replacement);
        var original = File.ReadAllText(CatalogPath);
        Assert.Throws<InvalidOperationException>(() => SnippetCatalogTransaction.Update(CatalogPath, _ => throw new InvalidOperationException()));
        Assert.Equal(original, File.ReadAllText(CatalogPath));
    }

    [Fact]
    public void MissingOrDeletedCatalogDoesNotGetRecreatedByOldUsage()
    {
        Assert.Equal(0, SnippetUsageRecorder.Record(CatalogPath, ["deleted"]));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void ExistingLexiconEditorAndAddImportKeepUsageFromLaterDictations()
    {
        Save(Entry("one", "trigger", "original"));
        var lexicon = new Lexicon(snippetPath: CatalogPath);
        var draft = Assert.Single(lexicon.Entries) with { Value = "edited" };
        SnippetUsageRecorder.Record(CatalogPath, ["one"]);
        Assert.Null(lexicon.Save(draft));
        Assert.Equal(1, Assert.Single(Read()).UsageCount);
        SnippetUsageRecorder.Record(CatalogPath, ["one"]);
        Assert.Null(lexicon.Import(JsonSerializer.Serialize(new[] { Entry("two", "another", "other") }), snippets: true, replace: false));
        Assert.Equal(2, Read().Single(entry => entry.Id == "one").UsageCount);
        Assert.Equal("edited", Read().Single(entry => entry.Id == "one").Replacement);
        Assert.Equal(2, lexicon.Entries.Single(entry => entry.Key == "trigger").UsageCount);
    }

    [Fact]
    public void OpenEditorDoesNotRestoreSnippetDeletedByAnotherMutation()
    {
        Save(Entry("one", "trigger", "original"));
        var lexicon = new Lexicon(snippetPath: CatalogPath);
        var draft = Assert.Single(lexicon.Entries) with { Value = "edited" };
        SnippetCatalogTransaction.Update(CatalogPath, _ => []);
        Assert.NotNull(lexicon.Save(draft));
        Assert.Empty(Read());
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
