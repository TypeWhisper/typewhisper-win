using TypeWhisper.Core.Models;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationWorkflowShortcutTests
{
    private static Workflow Workflow() => new()
    {
        Id = "translation", Name = "Translate", Template = WorkflowTemplate.Translation,
        Trigger = WorkflowTrigger.Hotkey(["CTRL+J"], WorkflowHotkeyBehavior.StartDictation),
        Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", TranslationTarget = "French" }
    };

    [Fact]
    public async Task ExplicitSnapshotSurvivesEditorChangesAndDoesNotBecomeAnAutomaticRule()
    {
        var workflow = Workflow();
        var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(workflow);
        var edited = workflow with { Behavior = workflow.Behavior with { ModelOverride = "other" } };
        Assert.Null(AutomaticWorkflowSnapshot.Select([workflow], "notepad"));
        var result = await snapshot.ProcessAsync("Hello", "en", "en", (p, m) => p == "provider" && m == "model",
            (p, prompt, text, model, ct) => Task.FromResult(model + ":" + text), default);
        Assert.Equal("model:Hello", result);
        Assert.Equal("other", edited.Behavior.ModelOverride);
    }

    [Fact]
    public void EditorRoundTripPreservesDictationActivationAndRejectsUnsupportedOverrides()
    {
        var workflow = Workflow();
        var draft = PrototypeWorkflow.FromStored(workflow);
        Assert.Equal("DictationHotkey", draft.ActivationId);
        Assert.True(ManualWorkflowStore.IsDictationShortcut(draft.ToStored()));
        Assert.Throws<InvalidOperationException>(() => AutomaticWorkflowSnapshot.ForDictationShortcut(workflow with { IsEnabled = false }));
        Assert.False(ManualWorkflowStore.IsDictationShortcut(workflow with
        { Behavior = workflow.Behavior with { TranscriptionModelOverride = "different" } }));
    }

    [Fact]
    public async Task SecondPressDuringStartStopsOriginalOverrideAndNextNormalStartUsesDefault()
    {
        var entered = new TaskCompletionSource();
        var barrier = new TaskCompletionSource();
        bool recording = false;
        int defaults = 0, stops = 0, competing = 0;
        using var input = new DictationInputCoordinator(
            () => { defaults++; recording = true; return Task.CompletedTask; },
            () => { stops++; recording = false; return Task.CompletedTask; },
            () => { recording = false; return Task.CompletedTask; },
            () => recording, () => true, () => RecordingMode.Toggle);
        var start = input.SubmitAsync(DictationInputAction.Start, async () =>
        { entered.SetResult(); await barrier.Task; recording = true; });
        await entered.Task;
        _ = input.SubmitAsync(DictationInputAction.Start, () => { competing++; return Task.CompletedTask; });
        _ = input.SubmitAsync(DictationInputAction.Toggle);
        barrier.SetResult();
        await start;
        Assert.Equal(1, stops); Assert.Equal(0, competing); Assert.Equal(0, defaults);
        await input.SubmitAsync(DictationInputAction.Start);
        Assert.Equal(1, defaults);
    }

    [Fact]
    public async Task CanceledQueuedStartDoesNotRunOrLeakOverride()
    {
        Action? queued = null;
        int explicitStarts = 0, defaults = 0;
        using var input = new DictationInputCoordinator(
            () => { defaults++; return Task.CompletedTask; }, () => Task.CompletedTask, () => Task.CompletedTask,
            () => false, () => true, () => RecordingMode.Toggle, action => { queued = action; return true; });
        var start = input.SubmitAsync(DictationInputAction.Start, () => { explicitStarts++; return Task.CompletedTask; });
        _ = input.SubmitAsync(DictationInputAction.Cancel);
        queued!(); await start;
        var normal = input.SubmitAsync(DictationInputAction.Start);
        queued!(); await normal;
        Assert.Equal(0, explicitStarts); Assert.Equal(1, defaults);
    }
}
