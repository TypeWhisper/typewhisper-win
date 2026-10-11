using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowAutoEnterTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-auto-enter-" + Guid.NewGuid());
    private string FilePath => Path.Join(_directory, "workflows.json");

    private static Workflow Rule(string id, WorkflowTrigger trigger, WorkflowAutoEnterMode mode) => new()
    {
        Id = id, Name = id, Template = WorkflowTemplate.Dictation, Trigger = trigger,
        Output = new WorkflowOutput().WithAutoEnterMode(mode)
    };

    [Fact]
    public void OffNeverTakesEnter()
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Off);

        Assert.False(intent.CanPress);
        Assert.False(intent.Press());
        Assert.False(intent.Requested);
    }

    [Fact]
    public void ArmedPressRequestsOnceAndClosingKeepsTheDecision()
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Armed);

        Assert.True(intent.Press());
        Assert.False(intent.CanPress);
        Assert.False(intent.Press());
        intent.Close();
        Assert.True(intent.Requested);
    }

    [Fact]
    public void ClosedRecordingLetsEnterThrough()
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Armed);
        intent.Close();

        Assert.False(intent.CanPress);
        Assert.False(intent.Requested);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PendingPressFollowsTheMatchedWorkflow(bool submits, bool requested)
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Pending);

        Assert.True(intent.Press());
        Assert.False(intent.Requested);
        intent.Resolve(submits);
        Assert.Equal(requested, intent.Requested);
        Assert.False(intent.CanPress);
    }

    [Fact]
    public void PendingWithoutMatchingRuleReleasesEnter()
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Pending);
        intent.Resolve(false);

        Assert.False(intent.CanPress);
    }

    [Theory]
    [InlineData(DictationSubmitArming.Armed, true)]
    [InlineData(DictationSubmitArming.Pending, false)]
    [InlineData(DictationSubmitArming.Off, false)]
    public void AdoptedCaptureKeepsAnEnterPressedWhileTheModelLoaded(DictationSubmitArming adopted, bool requested)
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Pending);
        intent.Press();
        intent.Continue(adopted);

        Assert.Equal(requested, intent.Requested);
        intent.Resolve(adopted != DictationSubmitArming.Off);
        Assert.Equal(adopted != DictationSubmitArming.Off, intent.Requested);
    }

    [Fact]
    public void NextRecordingStartsWithoutTheEarlierRequest()
    {
        var intent = new DictationSubmitIntent();
        intent.Begin(DictationSubmitArming.Armed);
        intent.Press();
        intent.Close();
        intent.Reset();
        intent.Begin(DictationSubmitArming.Armed);

        Assert.False(intent.Requested);
        Assert.True(intent.CanPress);
    }

    [Theory]
    [InlineData(WorkflowAutoEnterMode.SpokenCommand)]
    [InlineData(WorkflowAutoEnterMode.Always)]
    [InlineData(WorkflowAutoEnterMode.DuringDictation)]
    public void AutomaticRulesWithAnEnterModeAreSupported(WorkflowAutoEnterMode mode)
    {
        var snapshot = AutomaticWorkflowSnapshot.Select([Rule("chat", WorkflowTrigger.App("slack"), mode)], "slack")!;

        Assert.Null(snapshot.Error);
        Assert.Equal(mode, snapshot.AutoEnterMode);
        Assert.True(ManualWorkflowStore.IsEditable(Rule("chat", WorkflowTrigger.App("slack"), mode)));
    }

    [Fact]
    public void ShortcutWorkflowKeepsItsEnterMode()
    {
        var shortcut = Rule("shortcut", WorkflowTrigger.Hotkey(["CTRL+L"], WorkflowHotkeyBehavior.StartDictation), WorkflowAutoEnterMode.DuringDictation);

        Assert.Equal(WorkflowAutoEnterMode.DuringDictation, AutomaticWorkflowSnapshot.ForDictationShortcut(shortcut).AutoEnterMode);
    }

    [Fact]
    public void ActionTargetAndRejectedRulesNeverSubmit()
    {
        var action = Rule("action", WorkflowTrigger.Global(), WorkflowAutoEnterMode.Always) with
        { Output = new WorkflowOutput { TargetActionPluginId = "action" }.WithAutoEnterMode(WorkflowAutoEnterMode.Always) };

        Assert.Equal(WorkflowAutoEnterMode.Never, AutomaticWorkflowSnapshot.Select([action], "editor")!.AutoEnterMode);
        Assert.Equal(WorkflowAutoEnterMode.Never, AutomaticWorkflowSnapshot.Rejected("error").AutoEnterMode);
    }

    [Fact]
    public void UnknownEnterModeIsUnsupported()
    {
        var future = Rule("future", WorkflowTrigger.Global(), WorkflowAutoEnterMode.Never) with { Output = new() { AutoEnterModeRaw = "onDoubleEnter" } };

        Assert.NotNull(AutomaticWorkflowSnapshot.Select([future], "editor")!.Error);
        Assert.False(ManualWorkflowStore.IsEditable(future));
    }

    [Fact]
    public void OnlyEnabledAutomaticRulesWithoutActionMaySubmitDuringDictation()
    {
        var during = Rule("during", WorkflowTrigger.Website(["example.com"]), WorkflowAutoEnterMode.DuringDictation);

        Assert.True(AutomaticWorkflowSnapshot.MaySubmitDuringDictation([during]));
        Assert.False(AutomaticWorkflowSnapshot.MaySubmitDuringDictation([during with { IsEnabled = false }]));
        Assert.False(AutomaticWorkflowSnapshot.MaySubmitDuringDictation([during with { Trigger = WorkflowTrigger.Hotkey(["CTRL+L"], WorkflowHotkeyBehavior.StartDictation) }]));
        Assert.False(AutomaticWorkflowSnapshot.MaySubmitDuringDictation([during with { Output = during.Output with { TargetActionPluginId = "action" } }]));
        Assert.False(AutomaticWorkflowSnapshot.MaySubmitDuringDictation([Rule("always", WorkflowTrigger.Global(), WorkflowAutoEnterMode.Always)]));
    }

    [Fact]
    public void EditorDraftRoundTripsTheModeAndKeepsUnchangedOutput()
    {
        var stored = Rule("chat", WorkflowTrigger.App("slack"), WorkflowAutoEnterMode.SpokenCommand);
        var draft = WorkflowDraft.FromStored(stored);

        Assert.Equal(WorkflowAutoEnterMode.SpokenCommand, draft.AutoEnterMode);
        Assert.Same(stored.Output, draft.ToStored().Output);
        var changed = (draft with { AutoEnterMode = WorkflowAutoEnterMode.Always }).ToStored().Output;
        Assert.Equal("always", changed.AutoEnterModeRaw);
        Assert.True(changed.AutoEnter);
        var cleared = (draft with { AutoEnterMode = WorkflowAutoEnterMode.Never }).ToStored().Output;
        Assert.Null(cleared.AutoEnterModeRaw);
        Assert.False(cleared.AutoEnter);
    }

    [Fact]
    public void WorkflowFileKeepsTheMacCompatibleMode()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """
            [{"Id":"chat","Name":"Chat","Template":"Dictation","Trigger":{"Kind":"Global"},
              "Behavior":{"FineTuning":""},"Output":{"AutoEnter":false,"autoEnterModeRaw":"duringDictation"}}]
            """);
        var store = new ManualWorkflowStore(FilePath);

        var workflow = Assert.Single(store.Read());
        Assert.Equal(WorkflowAutoEnterMode.DuringDictation, workflow.Output.AutoEnterMode);
        store.Save(workflow with { Name = "Renamed" }, allowAutomatic: true);
        Assert.Equal(WorkflowAutoEnterMode.DuringDictation, Assert.Single(new ManualWorkflowStore(FilePath).Read()).Output.AutoEnterMode);
        Assert.Contains("\"autoEnterModeRaw\": \"duringDictation\"", File.ReadAllText(FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
