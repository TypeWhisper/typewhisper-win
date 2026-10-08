using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class CatalogConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catalog-concurrency-" + Guid.NewGuid());
    private string PathName => Path.Combine(_root, "catalog.json");
    public CatalogConcurrencyTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("dictionary")]
    [InlineData("snippet")]
    [InlineData("workflow")]
    public void StaleFullReplacementCannotEraseAnotherWritersAddition(string kind)
    {
        var stale = Open(kind);
        Assert.Equal(0, stale.Count()); // the file does not exist yet
        var writer = Open(kind);
        writer.Add("new entry");
        var expected = File.ReadAllBytes(PathName);

        Assert.False(stale.Clear());
        Assert.IsType<CatalogChangedException>(stale.Error());
        Assert.Equal(expected, File.ReadAllBytes(PathName));
        Assert.Equal(0, stale.Count());
        // Retrying the same stale operation must not authorize overwriting the newer list.
        Assert.False(stale.Clear());
        Assert.True(stale.Reload());
        Assert.Equal(1, stale.Count());
        stale.Add("another entry");
        Assert.Null(stale.Error());
        Assert.Equal(2, Open(kind).Count());
        // Successful writes advance the baseline, so consecutive writes still work.
        stale.Add("third entry");
        Assert.Null(stale.Error());
        Assert.Equal(3, Open(kind).Count());
    }

    [Theory]
    [InlineData("dictionary", false)]
    [InlineData("snippet", false)]
    [InlineData("workflow", false)]
    [InlineData("dictionary", true)]
    [InlineData("snippet", true)]
    [InlineData("workflow", true)]
    public void MutationCannotReplaceAFileChangedOrDeletedAfterLoading(string kind, bool deleted)
    {
        var stale = Open(kind);
        stale.Add("original");
        if (deleted) File.Delete(PathName);
        else File.WriteAllText(PathName, "{ interrupted external write");

        stale.Add("unsaved");

        Assert.IsType<CatalogChangedException>(stale.Error());
        Assert.Equal(1, stale.Count()); // failed mutations roll back
        if (deleted) Assert.False(File.Exists(PathName));
        else Assert.Equal("{ interrupted external write", File.ReadAllText(PathName));
    }

    [Fact]
    public void UsageCountsCannotOverwriteNewlyLearnedCorrections()
    {
        var stale = new DictionaryService(PathName);
        stale.AddEntry(new() { Id = "original", EntryType = DictionaryEntryType.Correction, Original = "teh", Replacement = "the" });
        var learning = new DictionaryService(PathName);
        Assert.Single(learning.LearnCorrections([new("adress", "address")]));
        var expected = File.ReadAllBytes(PathName);

        Assert.Equal("the", stale.ApplyCorrections("teh"));
        Assert.IsType<CatalogChangedException>(stale.LastSaveError);
        Assert.Equal(expected, File.ReadAllBytes(PathName));
    }

    private Catalog Open(string kind)
    {
        if (kind == "dictionary")
        {
            var service = new DictionaryService(PathName);
            return new(() => service.Entries.Count, name => service.AddEntry(new() { Id = name, EntryType = DictionaryEntryType.Term, Original = name }),
                () => service.TryReplaceAll([]), service.Reload, () => service.LastSaveError);
        }
        if (kind == "snippet")
        {
            var service = new SnippetService(PathName);
            return new(() => service.Snippets.Count, name => service.AddSnippet(new() { Id = name, Trigger = name, Replacement = "expanded" }),
                () => service.TryReplaceAll([]), service.Reload, () => service.LastSaveError);
        }
        var workflows = new WorkflowService(PathName);
        return new(() => workflows.Workflows.Count, name => workflows.AddWorkflow(new()
            { Id = name, Name = name, Trigger = new() { Kind = WorkflowTriggerKind.Manual }, Template = WorkflowTemplate.Dictation }),
            () => workflows.TryReplaceAll([]), workflows.Reload, () => workflows.LastSaveError);
    }

    private sealed record Catalog(Func<int> Count, Action<string> Add, Func<bool> Clear, Func<bool> Reload, Func<Exception?> Error);
}
