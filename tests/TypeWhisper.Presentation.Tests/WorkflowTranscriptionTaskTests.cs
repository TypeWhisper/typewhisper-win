using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowTranscriptionTaskTests
{
    private static Workflow Dictation(string? task, WorkflowTrigger? trigger = null) => new()
    {
        Id = "native-task", Name = "Native task", Template = WorkflowTemplate.Dictation,
        Trigger = trigger ?? WorkflowTrigger.Hotkey(["CTRL+J"], WorkflowHotkeyBehavior.StartDictation),
        Behavior = new() { SelectedTask = task }
    };

    [Theory]
    [InlineData(null, TranscriptionTask.Transcribe, TranscriptionTask.Transcribe)]
    [InlineData(null, TranscriptionTask.Translate, TranscriptionTask.Translate)]
    [InlineData("", TranscriptionTask.Translate, TranscriptionTask.Translate)]
    [InlineData(" ", TranscriptionTask.Transcribe, TranscriptionTask.Transcribe)]
    [InlineData("translate", TranscriptionTask.Transcribe, TranscriptionTask.Translate)]
    [InlineData("transcribe", TranscriptionTask.Translate, TranscriptionTask.Transcribe)]
    public void SnapshotUsesExplicitTaskOrCapturedGlobal(string? task, TranscriptionTask global, TranscriptionTask expected)
    {
        var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation(task));
        Assert.Equal(expected, WorkflowTranscriptionTask.Resolve(snapshot.SelectedTask, global, true));
    }

    [Theory]
    [InlineData("translate", TranscriptionTask.Transcribe)]
    [InlineData(null, TranscriptionTask.Translate)]
    public void EffectiveTranslationRejectsIncapableModel(string? task, TranscriptionTask global)
    {
        var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation(task));
        var error = Assert.Throws<NotSupportedException>(() => WorkflowTranscriptionTask.Resolve(snapshot.SelectedTask, global, false));
        Assert.Contains("cannot translate to English", error.Message);
    }

    [Fact]
    public void ExplicitTranscriptionWorksWhenGlobalTranslationIsUnsupported() =>
        Assert.Equal(TranscriptionTask.Transcribe, WorkflowTranscriptionTask.Resolve("transcribe", TranscriptionTask.Translate, false));

    [Theory]
    [InlineData("summarize")]
    [InlineData("Translate")]
    [InlineData("1")]
    public void UnknownTaskIsRejectedByAllRecordingEntryPoints(string task)
    {
        Assert.False(ManualWorkflowStore.IsDictationShortcut(Dictation(task)));
        Assert.Throws<InvalidOperationException>(() => AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation(task)));
        Assert.Throws<InvalidOperationException>(() => AutomaticWorkflowSnapshot.ForApi(Dictation(task, WorkflowTrigger.Manual())));
        Assert.Throws<InvalidOperationException>(() => WorkflowTranscriptionTask.Resolve(task, TranscriptionTask.Transcribe, true));
        var snapshot = AutomaticWorkflowSnapshot.Select([Dictation(task, WorkflowTrigger.Global())], "editor")!;
        Assert.NotNull(snapshot.Error);
        Assert.Throws<InvalidOperationException>(() => WorkflowTranscriptionTask.Resolve(snapshot.SelectedTask, TranscriptionTask.Transcribe, true));
    }

    [Fact]
    public void AppAndWebsitePrecedenceDeterminesRecordingTask()
    {
        var global = Dictation("translate", WorkflowTrigger.Global()) with { Id = "fallback" };
        var app = Dictation("transcribe", WorkflowTrigger.App("editor")) with { Id = "app" };
        var website = Dictation("translate", WorkflowTrigger.Website("example.com")) with { Id = "website" };
        Workflow[] rules = [global, app, website];
        var matched = AutomaticWorkflowSnapshot.Select(rules, "editor")!;
        Assert.Equal("app", matched.Id);
        Assert.Equal(TranscriptionTask.Transcribe, WorkflowTranscriptionTask.Resolve(matched.SelectedTask, TranscriptionTask.Translate, false));
        matched = AutomaticWorkflowSnapshot.Select(rules, "editor", "example.com")!;
        Assert.Equal("website", matched.Id);
        Assert.Equal(TranscriptionTask.Translate, WorkflowTranscriptionTask.Resolve(matched.SelectedTask, TranscriptionTask.Transcribe, true));
        Assert.Equal("translate", AutomaticWorkflowSnapshot.Select(rules, "other")!.SelectedTask);
        Assert.Null(AutomaticWorkflowSnapshot.Select([app], "other"));
    }

    [Fact]
    public void OnlyEnabledAutomaticTranscribeRulesDeferTheEarlyTranslationCheck()
    {
        Assert.False(WorkflowTranscriptionTask.AutomaticRuleMayTranscribe([]));
        Assert.False(WorkflowTranscriptionTask.AutomaticRuleMayTranscribe([
            Dictation("transcribe"),
            Dictation("transcribe", WorkflowTrigger.Manual()),
            Dictation("transcribe", WorkflowTrigger.Global()) with { IsEnabled = false },
            Dictation("translate", WorkflowTrigger.App("editor")),
            Dictation(null, WorkflowTrigger.Global())]));
        Assert.True(WorkflowTranscriptionTask.AutomaticRuleMayTranscribe([Dictation("transcribe", WorkflowTrigger.App("editor"))]));
        Assert.True(WorkflowTranscriptionTask.AutomaticRuleMayTranscribe([Dictation("transcribe", WorkflowTrigger.Website("example.com"))]));
        Assert.True(WorkflowTranscriptionTask.AutomaticRuleMayTranscribe([Dictation("transcribe", WorkflowTrigger.Global())]));
    }

    [Fact]
    public async Task NativeTranslationNeedsNoLlmAndNeverChangesGlobalPreference()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-task-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var preferencesPath = Path.Combine(directory, "task.json");
            var preferences = new TranscriptionTaskPreferencesStore(preferencesPath);
            Assert.Null(preferences.Save(TranscriptionTask.Transcribe));
            var savedPreference = File.ReadAllBytes(preferencesPath);
            var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(Dictation("translate"));
            Assert.Equal(TranscriptionTask.Translate, WorkflowTranscriptionTask.Resolve(snapshot.SelectedTask, preferences.Current, true));
            Assert.Equal("Translated audio", await snapshot.ProcessAsync("Translated audio", "de", "de",
                (_, _) => throw new Exception("No LLM required"),
                (_, _, _, _, _) => throw new Exception("No LLM required"), default));
            Assert.Throws<NotSupportedException>(() => WorkflowTranscriptionTask.Resolve(snapshot.SelectedTask, preferences.Current, false));
            // Both completed and rejected overrides leave the next ordinary recording unchanged.
            Assert.Equal(TranscriptionTask.Transcribe, WorkflowTranscriptionTask.Resolve(null, preferences.Current, false));
            Assert.Equal(savedPreference, File.ReadAllBytes(preferencesPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("transcribe")]
    [InlineData("translate")]
    public void EditorAndStorageRoundTripTaskWithoutChangingOtherSettings(string? task)
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-task-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ManualWorkflowStore(Path.Combine(directory, "workflows.json"));
            var workflow = Dictation(null);
            var draft = WorkflowDraft.FromStored(workflow) with { SelectedTask = task };
            store.Save(draft.ToStored(), allowAutomatic: true);
            var restored = WorkflowDraft.FromStored(Assert.Single(store.Read()));
            Assert.Equal(task, restored.SelectedTask);
            Assert.Equal(workflow.Template, restored.Template);
            Assert.Equal("CTRL+J", restored.Hotkeys);
            Assert.Equal(WorkflowHotkeyBehavior.StartDictation, restored.HotkeyBehavior);
            var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(restored.ToStored());
            store.Save((restored with { SelectedTask = task == "translate" ? "transcribe" : "translate" }).ToStored(), allowAutomatic: true);
            Assert.Equal(task, snapshot.SelectedTask);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OtherRecordingOverridesRemainUnsupported()
    {
        var workflow = Dictation("translate");
        Assert.False(ManualWorkflowStore.IsDictationShortcut(workflow with { Behavior = workflow.Behavior with { WhisperModeOverride = true } }));
        Assert.False(ManualWorkflowStore.IsDictationShortcut(workflow with { Behavior = workflow.Behavior with { TranscriptionModelOverride = "large-v3" } }));
        Assert.False(ManualWorkflowStore.IsSelectedTextShortcut(workflow with { Trigger = WorkflowTrigger.Hotkey(["CTRL+J"], WorkflowHotkeyBehavior.ProcessSelectedText) }));
    }
}
