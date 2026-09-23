using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowMemoryTests
{
    private static Workflow Example(string? source = "com.typewhisper.file-memory") => new()
    {
        Id = "memory", Name = "Memory example", Template = WorkflowTemplate.Custom,
        Trigger = WorkflowTrigger.Manual(), IsEnabled = true,
        Behavior = new() { FineTuning = "Write a project note.", ProviderOverride = "provider", ModelOverride = "model", MemoryPluginId = source }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualAndAutomaticPathsSupplyOnlyMatchingData(bool automatic)
    {
        var workflow = Example();
        var calls = 0;
        Task<IReadOnlyList<string>> Recall(string id, string query, CancellationToken ct)
        {
            Assert.Equal(workflow.Behavior.MemoryPluginId, id); Assert.Equal("Aurora update", query); calls++;
            return Task.FromResult<IReadOnlyList<string>>(["Aurora uses British English."]);
        }
        Task<string> Process(string provider, string prompt, string input, string model, CancellationToken ct)
        {
            Assert.Contains("untrusted reference facts", prompt);
            Assert.DoesNotContain("Aurora uses", prompt);
            using var json = JsonDocument.Parse(input);
            Assert.Equal("Aurora update", json.RootElement.GetProperty("sourceText").GetString());
            Assert.Equal("Aurora uses British English.", json.RootElement.GetProperty("memories")[0].GetString());
            return Task.FromResult("Completed note");
        }
        var result = automatic
            ? await AutomaticWorkflowSnapshot.ForApi(workflow).ProcessAsync("Aurora update", "de", null, (_, _) => true, Process, default, Recall)
            : await ManualWorkflowRunner.RunAsync(workflow, "Aurora update", (_, _) => true, Process, default, Recall);
        Assert.Equal("Completed note", result); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoOptInOrDictationOnlyNeverReadsMemory(bool dictation)
    {
        var workflow = dictation ? Example() with { Template = WorkflowTemplate.Dictation } : Example(null);
        var result = await ManualWorkflowRunner.RunAsync(workflow, "Original", (_, _) => true,
            (_, _, input, _, _) => Task.FromResult(input), default,
            (_, _, _) => throw new Exception("Memory was not opted into"));
        Assert.Equal("Original", result);
    }

    [Fact]
    public async Task MissingSourceAndCancellationStopBeforeLlm()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManualWorkflowRunner.RunAsync(Example(), "Original", (_, _) => true,
            (_, _, _, _, _) => throw new Exception("Must not process")));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualWorkflowRunner.RunAsync(Example(), "Original", (_, _) => true,
            (_, _, _, _, _) => throw new Exception("Must not process"), cancel.Token,
            (_, _, _) => { cancel.Cancel(); return Task.FromResult<IReadOnlyList<string>>(["Fact"]); }));
    }

    [Fact]
    public async Task EmptyMatchesLeaveInputUnchangedAndLargeMatchesAreBounded()
    {
        var unchanged = await WorkflowMemoryContext.PrepareAsync("memory", "prompt", "source", (_, _, _) => Task.FromResult<IReadOnlyList<string>>([]), default);
        Assert.Equal(("prompt", "source"), unchanged);
        var bounded = await WorkflowMemoryContext.PrepareAsync("memory", "prompt", "source",
            (_, _, _) => Task.FromResult<IReadOnlyList<string>>(Enumerable.Repeat(new string('x', 5000), 20).ToArray()), default);
        using var json = JsonDocument.Parse(bounded.Input);
        var facts = json.RootElement.GetProperty("memories");
        Assert.Equal(5, facts.GetArrayLength()); Assert.All(facts.EnumerateArray(), f => Assert.Equal(1600, f.GetString()!.Length));
    }

    [Fact]
    public void EditorRoundTripAndSnapshotPreserveMemorySelection()
    {
        var workflow = Example();
        var restored = WorkflowDraft.FromStored(workflow).ToStored();
        Assert.Equal(workflow.Behavior.MemoryPluginId, restored.Behavior.MemoryPluginId);
        Assert.Equal(workflow.Behavior.MemoryPluginId, AutomaticWorkflowSnapshot.ForApi(restored).MemoryPluginId);
        var without = WorkflowDraft.FromStored(workflow) with { MemoryPluginId = null };
        Assert.Null(without.ToStored().Behavior.MemoryPluginId);
    }
}
