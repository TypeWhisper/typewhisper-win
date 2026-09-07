using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ManualWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-workflows-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "workflows.json");
    private static Workflow Draft => new()
    {
        Id = "manual", Name = "My workflow", Template = WorkflowTemplate.Custom, Trigger = WorkflowTrigger.Manual(),
        Behavior = new() { FineTuning = "Preserve meaning.\nReturn a summary.", ProviderOverride = "package:role", ModelOverride = "exact-model" }
    };

    [Fact]
    public void SavePersistsAcrossRestartAndKeepsOtherWorkflowSemantics()
    {
        var automatic = Draft with { Id = "automatic", Template = WorkflowTemplate.Summary, Trigger = WorkflowTrigger.App("editor"),
            Output = new() { TargetActionPluginId = "action", AutoEnter = true }, SortOrder = 12 };
        Assert.True(new WorkflowService(FilePath).TryReplaceAll([automatic]));
        var store = new ManualWorkflowStore(FilePath);
        store.Save(Draft);
        store.Save(Draft with { Name = "Renamed" });
        var saved = new ManualWorkflowStore(FilePath).Read();
        var manual = Assert.Single(saved, w => w.Id == "manual");
        Assert.Equal("Renamed", manual.Name);
        Assert.Equal(Draft.Behavior.FineTuning, manual.Behavior.FineTuning);
        Assert.Equal(Draft.Behavior.ProviderOverride, manual.Behavior.ProviderOverride);
        Assert.Equal(Draft.Behavior.ModelOverride, manual.Behavior.ModelOverride);
        var retained = Assert.Single(saved, w => w.Id == "automatic");
        Assert.Equal("editor", Assert.Single(retained.Trigger.ProcessNames));
        Assert.Equal(automatic.Output, retained.Output);
        Assert.Equal(12, retained.SortOrder);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[{\"id\":\"x\",\"Id\":\"y\"}]")]
    [InlineData("[{\"id\":\"x\",\"name\":\"Name\",\"template\":\"Custom\",\"trigger\":{\"kind\":\"Manual\"},\"futureMetadata\":42}]")]
    public void MalformedOrFutureDataIsNotOverwritten(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json);
        Assert.Throws<JsonException>(() => new ManualWorkflowStore(FilePath).Save(Draft));
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void RejectedSaveReportsFailureAndKeepsPersistedSnapshot()
    {
        var store = new ManualWorkflowStore(FilePath);
        store.Save(Draft);
        var bytes = File.ReadAllBytes(FilePath);
        var originalSnapshot = store.Read();
        IReadOnlyList<Workflow>? candidate = null;
        var rejectingStore = new ManualWorkflowStore(FilePath, items => { candidate = items; return false; });
        Assert.Throws<IOException>(() => rejectingStore.Save(Draft with { Name = "Must not persist" }));
        Assert.Equal("Must not persist", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<Workflow>>(candidate)).Name);
        Assert.Equal(Draft.Name, Assert.Single(originalSnapshot).Name);
        Assert.Equal(Draft.Name, Assert.Single(rejectingStore.Read()).Name);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public async Task RunUsesExactProviderModelPromptAndEnteredInput()
    {
        const string input = "user-edited source\nsecond line";
        var result = await ManualWorkflowRunner.RunAsync(Draft, input,
            (provider, model) => provider == "package:role" && model == "exact-model",
            (provider, prompt, text, model, token) =>
            {
                Assert.Equal("package:role", provider);
                Assert.Equal("exact-model", model);
                Assert.Equal(Draft.SystemPrompt(), prompt);
                Assert.Equal(input, text);
                return Task.FromResult("provider result");
            });
        Assert.Equal("provider result", result);
    }

    [Fact]
    public async Task UnavailableChoiceNeverCallsAnotherProvider()
    {
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManualWorkflowRunner.RunAsync(Draft, "source", (_, _) => false,
            (_, _, _, _, _) => { called = true; return Task.FromResult("wrong"); }));
        Assert.False(called);
    }

    [Fact]
    public async Task CancellationRejectsLateProviderResult()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ManualWorkflowRunner.RunAsync(Draft, "source", (_, _) => true,
            (_, _, _, _, _) => { cancellation.Cancel(); return Task.FromResult("late"); }, cancellation.Token));
    }

    [Fact]
    public async Task FailureAndEmptyResponseProduceNoReplacementText()
    {
        await Assert.ThrowsAsync<IOException>(() => ManualWorkflowRunner.RunAsync(Draft, "source", (_, _) => true,
            (_, _, _, _, _) => throw new IOException("injected")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManualWorkflowRunner.RunAsync(Draft, "source", (_, _) => true,
            (_, _, _, _, _) => Task.FromResult("  ")));
    }

    [Fact]
    public void EnableAndDeletePersistAndPreserveUnsupportedWorkflows()
    {
        var automatic = Draft with { Id = "automatic", Trigger = WorkflowTrigger.App("editor") };
        Assert.True(new WorkflowService(FilePath).TryReplaceAll([automatic]));
        var store = new ManualWorkflowStore(FilePath);
        store.Save(Draft with { IsEnabled = false });
        Assert.False(Assert.Single(new ManualWorkflowStore(FilePath).Read(), w => w.Id == Draft.Id).IsEnabled);
        store.Save(Draft with { IsEnabled = true });
        Assert.True(Assert.Single(new ManualWorkflowStore(FilePath).Read(), w => w.Id == Draft.Id).IsEnabled);
        store.Delete(Draft.Id);
        var remaining = Assert.Single(new ManualWorkflowStore(FilePath).Read());
        Assert.Equal("automatic", remaining.Id);
        Assert.Equal("editor", Assert.Single(remaining.Trigger.ProcessNames));
    }

    [Fact]
    public void StaleEditorCannotOverwriteOrDeleteNewlyUnsupportedWorkflow()
    {
        var store = new ManualWorkflowStore(FilePath);
        store.Save(Draft);
        Assert.True(new WorkflowService(FilePath).TryReplaceAll([Draft with { Output = new() { TargetActionPluginId = "action" } }]));
        var bytes = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidOperationException>(() => store.Save(Draft with { IsEnabled = false }));
        Assert.Throws<InvalidOperationException>(() => store.Delete(Draft.Id));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void RejectedDisableAndDeleteReportFailureAndKeepPersistedSnapshot()
    {
        var store = new ManualWorkflowStore(FilePath);
        store.Save(Draft);
        var bytes = File.ReadAllBytes(FilePath);
        var originalSnapshot = store.Read();
        var candidates = new List<IReadOnlyList<Workflow>>();
        var rejectingStore = new ManualWorkflowStore(FilePath, items => { candidates.Add(items); return false; });
        Assert.Throws<IOException>(() => rejectingStore.Save(Draft with { IsEnabled = false }));
        Assert.Throws<IOException>(() => rejectingStore.Delete(Draft.Id));
        Assert.Equal(2, candidates.Count);
        Assert.False(Assert.Single(candidates[0]).IsEnabled);
        Assert.Empty(candidates[1]);
        Assert.True(Assert.Single(originalSnapshot).IsEnabled);
        Assert.True(Assert.Single(rejectingStore.Read()).IsEnabled);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.True(Assert.Single(new ManualWorkflowStore(FilePath).Read()).IsEnabled);
    }

    [Fact]
    public async Task DisabledWorkflowNeverCallsProvider()
    {
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ManualWorkflowRunner.RunAsync(Draft with { IsEnabled = false }, "source", (_, _) => true,
            (_, _, _, _, _) => { called = true; return Task.FromResult("wrong"); }));
        Assert.False(called);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
