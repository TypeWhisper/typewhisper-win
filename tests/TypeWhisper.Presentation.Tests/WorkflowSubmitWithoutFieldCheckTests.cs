using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Games and other apps without accessibility support never confirm their text field. A workflow can opt in to the
// automatic Enter there; without the option, Enter only follows a confirmed paste.
public sealed class WorkflowSubmitWithoutFieldCheckTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-submit-unconfirmed-" + Guid.NewGuid());
    private string FilePath => Path.Join(_directory, "workflows.json");

    private static Workflow Rule(WorkflowAutoEnterMode mode, bool unconfirmed, string? action = null) => new()
    {
        Id = "game", Name = "Game chat", Template = WorkflowTemplate.Dictation, Trigger = WorkflowTrigger.App("wow"),
        Output = new WorkflowOutput { SubmitWithoutFieldCheck = unconfirmed, TargetActionPluginId = action }.WithAutoEnterMode(mode)
    };

    [Theory]
    [InlineData(WorkflowAutoEnterMode.Always, true, true)]
    [InlineData(WorkflowAutoEnterMode.SpokenCommand, true, true)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation, true, false)]
    [InlineData(WorkflowAutoEnterMode.Never, true, false)]
    [InlineData(WorkflowAutoEnterMode.Always, false, false)]
    public void OnlyModesThatPressEnterThemselvesUseTheOption(WorkflowAutoEnterMode mode, bool stored, bool expected) =>
        Assert.Equal(expected, AutomaticWorkflowSnapshot.Select([Rule(mode, stored)], "wow")!.SubmitWithoutFieldCheck);

    [Fact]
    public void PluginActionsAndRejectedWorkflowsNeverSubmit()
    {
        Assert.False(AutomaticWorkflowSnapshot.Select([Rule(WorkflowAutoEnterMode.Always, true, "action")], "wow")!.SubmitWithoutFieldCheck);
        Assert.False(AutomaticWorkflowSnapshot.Rejected("error").SubmitWithoutFieldCheck);
    }

    [Fact]
    public void EditorDraftRoundTripsTheOptionAndDropsItForOtherModes()
    {
        var stored = Rule(WorkflowAutoEnterMode.Always, true);
        var draft = WorkflowDraft.FromStored(stored);

        Assert.True(draft.SubmitWithoutFieldCheck);
        Assert.Same(stored.Output, draft.ToStored().Output);
        Assert.False((draft with { SubmitWithoutFieldCheck = false }).ToStored().Output.SubmitWithoutFieldCheck);
        Assert.False((draft with { AutoEnterMode = WorkflowAutoEnterMode.DuringDictation }).ToStored().Output.SubmitWithoutFieldCheck);
    }

    [Fact]
    public void WorkflowFileOmitsTheOptionWhileItIsOff()
    {
        Directory.CreateDirectory(_directory);
        var store = new ManualWorkflowStore(FilePath);

        store.Save(Rule(WorkflowAutoEnterMode.Always, false), allowAutomatic: true);
        Assert.DoesNotContain("SubmitWithoutFieldCheck", File.ReadAllText(FilePath));

        store.Save(Rule(WorkflowAutoEnterMode.Always, true), allowAutomatic: true);
        Assert.Contains("\"SubmitWithoutFieldCheck\": true", File.ReadAllText(FilePath));
        Assert.True(Assert.Single(new ManualWorkflowStore(FilePath).Read()).Output.SubmitWithoutFieldCheck);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
