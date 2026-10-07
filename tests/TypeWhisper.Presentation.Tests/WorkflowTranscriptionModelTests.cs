using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowTranscriptionModelTests
{
    private const string Model = "local:parakeet-tdt-0.6b-v3";

    private static Workflow Workflow(string? model, WorkflowTemplate template = WorkflowTemplate.Dictation, WorkflowTrigger? trigger = null) => new()
    {
        Id = "workflow-model", Name = "Workflow model", Template = template,
        Trigger = trigger ?? WorkflowTrigger.Hotkey(["CTRL+F9"], WorkflowHotkeyBehavior.StartDictation),
        Behavior = new() { TranscriptionModelOverride = model, ProviderOverride = "provider", ModelOverride = "llm" }
    };

    [Fact]
    public void DictationShortcutCarriesItsModel()
    {
        Assert.True(ManualWorkflowStore.IsDictationShortcut(Workflow(Model)));
        Assert.Equal(Model, AutomaticWorkflowSnapshot.ForDictationShortcut(Workflow(Model)).TranscriptionModel);
        Assert.Null(AutomaticWorkflowSnapshot.ForDictationShortcut(Workflow(null)).TranscriptionModel);
        Assert.Null(AutomaticWorkflowSnapshot.ForDictationShortcut(Workflow(" ")).TranscriptionModel);
    }

    [Fact]
    public void ApiStartsOfDictationWorkflowsCarryTheirModel() =>
        Assert.Equal(Model, AutomaticWorkflowSnapshot.ForApi(Workflow(Model, trigger: WorkflowTrigger.Manual())).TranscriptionModel);

    [Theory]
    [InlineData(WorkflowTemplate.CleanedText)]
    [InlineData(WorkflowTemplate.Translation)]
    public void OnlyDictationOnlyWorkflowsCanSwitchModels(WorkflowTemplate template)
    {
        Assert.False(ManualWorkflowStore.IsDictationShortcut(Workflow(Model, template)));
        Assert.Throws<InvalidOperationException>(() => AutomaticWorkflowSnapshot.ForDictationShortcut(Workflow(Model, template)));
    }

    [Fact]
    public void UnqualifiedModelIsUnsupported() =>
        Assert.False(ManualWorkflowStore.IsDictationShortcut(Workflow("parakeet-tdt-0.6b-v3")));

    [Fact]
    public void AutomaticRulesCannotSwitchModelsAfterCaptureStarts()
    {
        var snapshot = AutomaticWorkflowSnapshot.Select([Workflow(Model, trigger: WorkflowTrigger.App("editor"))], "editor")!;
        Assert.NotNull(snapshot.Error);
        Assert.False(ManualWorkflowStore.IsEditable(Workflow(Model, trigger: WorkflowTrigger.Global())));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Model)]
    public void EditorAndStorageRoundTripModel(string? model)
    {
        var directory = Path.Join(Path.GetTempPath(), "workflow-model-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ManualWorkflowStore(Path.Join(directory, "workflows.json"));
            var draft = WorkflowDraft.FromStored(Workflow(null)) with { TranscriptionModel = model, InputLanguage = "de" };
            store.Save(draft.ToStored(), allowAutomatic: true);
            var restored = WorkflowDraft.FromStored(Assert.Single(store.Read()));
            Assert.Equal(model, restored.TranscriptionModel);
            Assert.Equal("de", restored.InputLanguage);
            Assert.Equal(model, AutomaticWorkflowSnapshot.ForDictationShortcut(restored.ToStored()).TranscriptionModel);
        }
        finally { Directory.Delete(directory, true); }
    }
}
