using System.Text;
using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LocalApiDataHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-api-data-" + Guid.NewGuid().ToString("N"));
    private string DictionaryPath => Path.Combine(_directory, "dictionary.json");
    private string WorkflowPath => Path.Combine(_directory, "workflows.json");
    public LocalApiDataHandlerTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private LocalApiResponse Call(string method, string path, string? body = null, IReadOnlyDictionary<string, string?>? query = null) =>
        new LocalApiDataHandler(DictionaryPath, WorkflowPath).Handle(new(method, path,
            Encoding.UTF8.GetBytes(body ?? ""), body is null ? null : "application/json", query ?? new Dictionary<string, string?>()))!;
    private DictionaryEntry[] Read() => LexiconTransfer.ReadDictionary(File.ReadAllText(DictionaryPath), allowPackEntries: true);

    [Fact]
    public void ReplaceTermsPreservesPacksAndCorrectionMetadata()
    {
        var correction = new DictionaryEntry { Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Correction,
            Original = "get hub", Replacement = "GitHub", UsageCount = 13, Source = DictionaryEntrySource.AutoLearned,
            UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        File.WriteAllText(DictionaryPath, LexiconTransfer.WriteDictionary([correction,
            new() { Id = "pack:test:WinUI", EntryType = DictionaryEntryType.Term, Original = "WinUI" },
            new() { Id = Guid.NewGuid().ToString(), EntryType = DictionaryEntryType.Term, Original = "Old" }]));
        var response = Call("PUT", "/v1/dictionary/terms", """{"term_entries":[{"term":"TypeWhisper","ctc_min_similarity":0.8}],"replace":true}""");
        Assert.Equal(200, response.StatusCode);
        var saved = Read();
        Assert.Contains(saved, entry => entry.Id.StartsWith("pack:", StringComparison.Ordinal));
        Assert.Equal(correction, saved.Single(entry => entry.Id == correction.Id));
        Assert.DoesNotContain(saved, entry => entry.Original == "Old");
        Assert.Equal(.8f, saved.Single(entry => entry.Original == "TypeWhisper").CtcMinSimilarity);
        Assert.Equal(2, JsonDocument.Parse(response.Body).RootElement.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("{\"terms\":[\"Good\",\"\"],\"replace\":true}")]
    [InlineData("{\"term_entries\":[{\"term\":\"Bad\",\"ctc_min_similarity\":\"wrong\"}]}")]
    [InlineData("{\"term_entries\":[{\"term\":\"Bad\",\"ctc_min_similarity\":0.1}]}")]
    [InlineData("{\"terms\":[\"Good\"],\"terms\":[\"Other\"]}")]
    public void InvalidBatchDoesNotPartiallyWrite(string body)
    {
        Assert.Equal(200, Call("PUT", "/v1/dictionary/terms", """{"terms":["Original"]}""").StatusCode);
        var before = File.ReadAllBytes(DictionaryPath);
        Assert.Equal(400, Call("PUT", "/v1/dictionary/terms", body).StatusCode);
        Assert.Equal(before, File.ReadAllBytes(DictionaryPath));
    }

    [Fact]
    public void CorrectionsSupportEmptyReplacementAndDeleteJsonContract()
    {
        Assert.Equal(200, Call("PUT", "/v1/dictionary/corrections", """{"original":"filler","replacement":"","caseSensitive":true}""").StatusCode);
        using var result = JsonDocument.Parse(Call("GET", "/v1/dictionary/corrections").Body);
        var correction = result.RootElement.GetProperty("corrections")[0];
        Assert.Equal("", correction.GetProperty("replacement").GetString());
        Assert.True(correction.GetProperty("caseSensitive").GetBoolean());
        using var deleted = JsonDocument.Parse(Call("DELETE", "/v1/dictionary/corrections", """{"original":"filler"}""").Body);
        Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());
        Assert.Empty(Read());
    }

    [Fact]
    public void ProfileAliasTogglesRealWorkflowAndPreservesConfiguration()
    {
        var id = Guid.NewGuid().ToString();
        var workflow = new Workflow { Id = id, Name = "Browser", Template = WorkflowTemplate.Custom,
            Trigger = new() { Kind = WorkflowTriggerKind.App, ProcessNames = ["msedge"] },
            Behavior = new() { FineTuning = "Keep this", InputLanguage = "de", TranslationTarget = "en" } };
        File.WriteAllText(WorkflowPath, JsonSerializer.Serialize(new[] { workflow }));
        var profiles = Call("GET", "/v1/profiles");
        Assert.Equal(200, profiles.StatusCode);
        Assert.Equal(Call("GET", "/v1/rules").Body, profiles.Body);
        using var response = JsonDocument.Parse(Call("PUT", "/v1/profiles/toggle", query: new Dictionary<string, string?> { ["id"] = id }).Body);
        Assert.False(response.RootElement.GetProperty("is_enabled").GetBoolean());
        var updated = new ManualWorkflowStore(WorkflowPath).Read().Single();
        Assert.False(updated.IsEnabled);
        Assert.Equal("Keep this", updated.Behavior.FineTuning);
        Assert.Equal("msedge", updated.Trigger.ProcessNames.Single());
    }

    [Fact]
    public void RejectsMalformedStorageAndWrongMethodsWithoutWriting()
    {
        File.WriteAllText(DictionaryPath, "invalid");
        Assert.Equal(500, Call("PUT", "/v1/dictionary/terms", """{"terms":["New"]}""").StatusCode);
        Assert.Equal("invalid", File.ReadAllText(DictionaryPath));
        Assert.Equal(405, Call("POST", "/v1/profiles").StatusCode);
        Assert.Equal(400, Call("GET", "/v1/dictionary/terms", query: new Dictionary<string, string?> { ["x"] = "y" }).StatusCode);
    }
}
