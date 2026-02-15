using TypeWhisper.Core.Data;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class SnippetServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TypeWhisperDatabase _db;
    private readonly SnippetService _sut;

    public SnippetServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tw_test_{Guid.NewGuid():N}.db");
        _db = new TypeWhisperDatabase(_dbPath);
        _db.Initialize();
        _sut = new SnippetService(_db);
    }

    [Fact]
    public void AddSnippet_WithTags_PersistsAndLoads()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "mfg",
            Replacement = "Mit freundlichen Grüßen",
            Tags = "E-Mail,Gruß"
        });

        // Force reload from DB
        var freshService = new SnippetService(_db);
        var snippet = Assert.Single(freshService.Snippets);
        Assert.Equal("E-Mail,Gruß", snippet.Tags);
    }

    [Fact]
    public void ApplySnippets_ClipboardPlaceholder_ExpandsFromProvider()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "link",
            Replacement = "Siehe: {clipboard}"
        });

        var result = _sut.ApplySnippets("link", () => "https://example.com");
        Assert.Equal("Siehe: https://example.com", result);
    }

    [Fact]
    public void ApplySnippets_ClipboardPlaceholder_EmptyWhenNoProvider()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "link",
            Replacement = "Siehe: {clipboard}"
        });

        var result = _sut.ApplySnippets("link");
        Assert.Equal("Siehe: ", result);
    }

    [Fact]
    public void ApplySnippets_CustomDateFormat_ExpandsCorrectly()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "heute",
            Replacement = "{date:dd.MM.yyyy}"
        });

        var result = _sut.ApplySnippets("heute");
        Assert.Equal(DateTime.Now.ToString("dd.MM.yyyy"), result);
    }

    [Fact]
    public void ApplySnippets_CustomTimeFormat_ExpandsCorrectly()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "uhr",
            Replacement = "{time:HH:mm:ss}"
        });

        var result = _sut.ApplySnippets("uhr");
        // Allow 1 second tolerance
        var expected = DateTime.Now.ToString("HH:mm:ss");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ApplySnippets_StandardPlaceholders_StillWork()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "datum",
            Replacement = "{date}"
        });
        _sut.AddSnippet(new Snippet
        {
            Id = "2",
            Trigger = "zeit",
            Replacement = "{time}"
        });
        _sut.AddSnippet(new Snippet
        {
            Id = "3",
            Trigger = "tag",
            Replacement = "{day}"
        });
        _sut.AddSnippet(new Snippet
        {
            Id = "4",
            Trigger = "jahr",
            Replacement = "{year}"
        });

        var now = DateTime.Now;
        Assert.Equal(now.ToString("yyyy-MM-dd"), _sut.ApplySnippets("datum"));
        Assert.Equal(now.ToString("HH:mm"), _sut.ApplySnippets("zeit"));
        Assert.Equal(now.ToString("dddd"), _sut.ApplySnippets("tag"));
        Assert.Equal(now.Year.ToString(), _sut.ApplySnippets("jahr"));
    }

    [Fact]
    public void AllTags_ReturnsDistinctSortedTags()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "a", Replacement = "A", Tags = "Code,E-Mail" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "b", Replacement = "B", Tags = "E-Mail,Datum" });
        _sut.AddSnippet(new Snippet { Id = "3", Trigger = "c", Replacement = "C", Tags = "" });

        var tags = _sut.AllTags;
        Assert.Equal(3, tags.Count);
        Assert.Equal("Code", tags[0]);
        Assert.Equal("Datum", tags[1]);
        Assert.Equal("E-Mail", tags[2]);
    }

    [Fact]
    public void ExportToJson_ReturnsValidJson()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "mfg", Replacement = "Grüße", Tags = "E-Mail" });
        _sut.AddSnippet(new Snippet { Id = "2", Trigger = "sig", Replacement = "Signatur\nZeile 2" });

        var json = _sut.ExportToJson();

        Assert.Contains("mfg", json);
        Assert.Contains("sig", json);
        Assert.Contains("E-Mail", json);
    }

    [Fact]
    public void ImportFromJson_AddsSnippets()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "existing", Replacement = "Existing" });

        var json = """
        [
            {"Id":"x","Trigger":"neu","Replacement":"Neuer Snippet","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"Import","CreatedAt":"2026-01-01T00:00:00"}
        ]
        """;

        var count = _sut.ImportFromJson(json);
        Assert.Equal(1, count);
        Assert.Equal(2, _sut.Snippets.Count);
        Assert.Contains(_sut.Snippets, s => s.Trigger == "neu");
    }

    [Fact]
    public void ImportFromJson_SkipsDuplicateTriggers()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "mfg", Replacement = "Grüße" });

        var json = """
        [
            {"Id":"x","Trigger":"mfg","Replacement":"Anderer Text","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"","CreatedAt":"2026-01-01T00:00:00"},
            {"Id":"y","Trigger":"neu","Replacement":"Neuer Text","CaseSensitive":false,"IsEnabled":true,"UsageCount":0,"Tags":"","CreatedAt":"2026-01-01T00:00:00"}
        ]
        """;

        var count = _sut.ImportFromJson(json);
        Assert.Equal(1, count); // only "neu" imported, "mfg" skipped
        Assert.Equal(2, _sut.Snippets.Count);
    }

    [Fact]
    public void SchemaIncludesTagsColumn()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(snippets)";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("tags", columns);
    }

    [Fact]
    public void ApplySnippets_MultilineReplacement_Works()
    {
        _sut.AddSnippet(new Snippet
        {
            Id = "1",
            Trigger = "sig",
            Replacement = "Mit freundlichen Grüßen\nMarco Mustermann\nTypeWhisper GmbH"
        });

        var result = _sut.ApplySnippets("sig");
        Assert.Equal("Mit freundlichen Grüßen\nMarco Mustermann\nTypeWhisper GmbH", result);
    }

    [Fact]
    public void UpdateSnippet_WithTags_PersistsChanges()
    {
        _sut.AddSnippet(new Snippet { Id = "1", Trigger = "mfg", Replacement = "Grüße", Tags = "Alt" });
        _sut.UpdateSnippet(new Snippet { Id = "1", Trigger = "mfg", Replacement = "Grüße", Tags = "Neu" });

        var freshService = new SnippetService(_db);
        Assert.Equal("Neu", freshService.Snippets[0].Tags);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
