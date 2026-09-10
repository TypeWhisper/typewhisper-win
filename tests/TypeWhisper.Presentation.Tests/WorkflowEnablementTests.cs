using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowEnablementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workflow-enable-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_directory, "workflows.json");
    private static Workflow Unsupported() => new()
    {
        Id = "imported", Name = "Imported rule", Template = WorkflowTemplate.Custom,
        Trigger = WorkflowTrigger.App("notepad"), SortOrder = -12,
        Behavior = new() { FineTuning = "Original", InputLanguage = "de", Settings = new() { ["foreign-option"] = "keep" } },
        Output = new() { TargetActionPluginId = "foreign.action", AutoEnter = true }
    };

    [Fact]
    public void EnablementUsesLatestRecordAndChangesNoOtherMetadata()
    {
        Directory.CreateDirectory(_directory);
        var original = Unsupported();
        Assert.True(new WorkflowService(StorePath).TryReplaceAll([original]));
        var store = new ManualWorkflowStore(StorePath);
        var draft = WorkflowDraft.FromStored(Assert.Single(store.Read()));
        Assert.False(draft.IsEditable);
        Assert.Contains("Unsupported", draft.Description);
        var latest = original with { Name = "Concurrent rename", Behavior = original.Behavior with { FineTuning = "Concurrent instructions" } };
        Assert.True(new WorkflowService(StorePath).TryReplaceAll([latest]));
        var disabled = store.SetEnabled(draft.Id, false);
        Assert.False(disabled.IsEnabled);
        Assert.Equal(JsonSerializer.Serialize(latest), JsonSerializer.Serialize(disabled with { IsEnabled = true }));
        var reopened = Assert.Single(new ManualWorkflowStore(StorePath).Read());
        Assert.Equal(JsonSerializer.Serialize(disabled), JsonSerializer.Serialize(reopened));
        Assert.Equal(JsonSerializer.Serialize(latest), JsonSerializer.Serialize(store.SetEnabled(draft.Id, true)));
    }

    [Fact]
    public void FailedWriteAndDeletedRecordDoNotOverwriteTheCatalog()
    {
        Directory.CreateDirectory(_directory);
        Assert.True(new WorkflowService(StorePath).TryReplaceAll([Unsupported()]));
        var before = File.ReadAllBytes(StorePath);
        Assert.Throws<IOException>(() => new ManualWorkflowStore(StorePath, _ => false).SetEnabled("imported", false));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
        Assert.Throws<InvalidOperationException>(() => new ManualWorkflowStore(StorePath).SetEnabled("missing", false));
        Assert.Equal(before, File.ReadAllBytes(StorePath));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
