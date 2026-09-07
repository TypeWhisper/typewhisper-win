using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using Xunit;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.WinUI;

namespace TypeWhisper.Presentation.Tests;

public sealed class AutomaticWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealDeliveryNeverPastesFailedWorkflowAndRespectsHistoryPrivacy(bool save)
    {
        var pipeline = await DictationTextPipeline.ProcessAsync("retained transcript", new(), "en",
            workflow: (_, _) => Task.FromException<string>(new IOException("provider failed")));
        var record = new TranscriptionRecord
        {
            Id = "workflow-failure", Timestamp = DateTime.UtcNow, RawText = "retained transcript", FinalText = pipeline.Text,
            WorkflowId = "selected", ProfileName = "Selected workflow",
            Status = TranscriptionRecordStatus.WorkflowPostProcessingFailed, WorkflowFailureMessage = pipeline.WorkflowError
        };
        var history = new Mock<IHistoryService>(MockBehavior.Strict);
        if (save)
        {
            history.Setup(h => h.EnsureLoadedAsync()).Returns(Task.CompletedTask);
            history.Setup(h => h.TryAddRecord(record)).Returns(true);
        }
        var preferences = new DictationOutputPreferences { AutoPaste = true, SaveToHistory = save };
        var outcome = await new DictationOutputDelivery(history.Object).DeliverAsync(record, preferences, () => preferences,
            () => throw new Exception("A failed workflow must never paste"));
        Assert.True(outcome.NeedsReview);
        Assert.Equal(save, outcome.Saved);
        Assert.Equal("retained transcript", outcome.Record.FinalText);
        Assert.NotNull(outcome.Record.WorkflowFailureMessage);
        history.Verify(h => h.TryAddRecord(It.IsAny<TranscriptionRecord>()), save ? Times.Once() : Times.Never());
    }

    [Fact]
    public void AppEditorRoundTripPreservesUnsupportedCatalogEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "automatic-workflows-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "workflows.json");
            var foreign = Rule("foreign", new() { Kind = WorkflowTriggerKind.Website, WebsitePatterns = ["example.com"] });
            Assert.True(new WorkflowService(path).TryReplaceAll([foreign]));
            var draft = PrototypeWorkflow.FromStored(Rule("app", WorkflowTrigger.App("notepad"), -5));
            var store = new ManualWorkflowStore(path);
            store.Save(draft.ToStored(), allowAutomatic: true);
            var saved = store.Read();
            var restored = PrototypeWorkflow.FromStored(saved.Single(item => item.Id == "app"));
            Assert.Equal(WorkflowTriggerKind.App, restored.TriggerKind);
            Assert.Equal("notepad", restored.AppProcesses);
            Assert.Equal(-5, restored.Priority);
            var preserved = saved.Single(item => item.Id == "foreign");
            Assert.Equal(WorkflowTriggerKind.Website, preserved.Trigger.Kind);
            Assert.Equal(foreign.Trigger.WebsitePatterns, preserved.Trigger.WebsitePatterns);
            Assert.Equal(foreign.Behavior.FineTuning, preserved.Behavior.FineTuning);
            Assert.Throws<InvalidOperationException>(() => store.Delete("foreign", allowAutomatic: true));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Workflow Rule(string id, WorkflowTrigger trigger, int order = 0) => new()
    {
        Id = id, Name = id, Trigger = trigger, SortOrder = order, Template = WorkflowTemplate.Custom,
        Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", FineTuning = "Keep names." }
    };

    [Fact]
    public void SelectionUsesCorePrecedenceAndIgnoresDisabledManualAndHotkeyRules()
    {
        var global = Rule("global", WorkflowTrigger.Global(), -100);
        var app = Rule("app", WorkflowTrigger.App("editor"), 10);
        var workflows = new[] { global, app, Rule("disabled", WorkflowTrigger.App("editor"), -10) with { IsEnabled = false },
            Rule("manual", WorkflowTrigger.Manual(), -20), Rule("hotkey", new() { Kind = WorkflowTriggerKind.Hotkey, ProcessNames = ["editor"] }, -30) };
        Assert.Equal("app", AutomaticWorkflowSnapshot.Select(workflows, "EDITOR")!.Id);
        Assert.Equal("global", AutomaticWorkflowSnapshot.Select(workflows, "other")!.Id);
        Assert.Null(AutomaticWorkflowSnapshot.Select([app], "other"));
        Assert.Equal("app", WorkflowService.MatchSnapshot([global, app], "EDITOR", null)!.Workflow.Id);
    }

    [Fact]
    public void EqualPriorityUsesCoreNameOrder()
    {
        Assert.Equal("Alpha", AutomaticWorkflowSnapshot.Select(
            [Rule("Zulu", WorkflowTrigger.Global()), Rule("Alpha", WorkflowTrigger.Global())], null)!.Id);
    }

    [Fact]
    public async Task SnapshotDoesNotRetainMutableCatalogCollections()
    {
        var names = new List<string> { "editor" };
        var rule = Rule("selected", new() { Kind = WorkflowTriggerKind.App, ProcessNames = names });
        var snapshot = AutomaticWorkflowSnapshot.Select([rule], "editor")!;
        names.Clear();
        rule.Behavior.Settings["instruction"] = "Changed after start";
        var text = await snapshot.ProcessAsync("source", "en", "en", (p, m) => p == "provider" && m == "model",
            (provider, prompt, input, model, _) =>
            {
                Assert.Contains("Keep names.", prompt);
                Assert.Equal(Rule("selected", WorkflowTrigger.Global()).SystemPrompt(configuredLanguage: "en", detectedLanguage: "en"), prompt);
                Assert.DoesNotContain("Changed after start", prompt);
                Assert.Equal("source", input);
                return Task.FromResult("processed");
            }, default);
        Assert.Equal("processed", text);
    }

    [Fact]
    public async Task UnsupportedSelectedRuleDoesNotFallBackOrInvokeProvider()
    {
        var rule = Rule("selected", WorkflowTrigger.App("editor")) with { Output = new() { AutoEnter = true } };
        var snapshot = AutomaticWorkflowSnapshot.Select([rule, Rule("fallback", WorkflowTrigger.Global())], "editor")!;
        Assert.Equal("selected", snapshot.Id);
        Assert.NotNull(snapshot.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.ProcessAsync("source", null, null,
            (_, _) => throw new Exception("Must not check provider"),
            (_, _, _, _, _) => throw new Exception("Must not invoke provider"), default));
    }

    [Fact]
    public async Task MissingExactProviderRetainsTextAndStopsBeforeSnippets()
    {
        var snapshot = AutomaticWorkflowSnapshot.Select([Rule("selected", WorkflowTrigger.Global())], null)!;
        var result = await DictationTextPipeline.ProcessAsync("source", new(), "en",
            expandSnippets: (_, _) => throw new Exception("Must not expand snippets"),
            workflow: (text, ct) => snapshot.ProcessAsync(text, "en", null, (_, _) => false,
                (_, _, _, _, _) => throw new Exception("Must not invoke provider"), ct));
        Assert.Equal("source", result.Text);
        Assert.NotNull(result.WorkflowError);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task WorkflowRunsBeforeSnippetsBoostingAndCorrections()
    {
        var steps = new List<string>();
        var result = await DictationTextPipeline.ProcessAsync("source", new(), "en",
            workflow: (text, _) => { steps.Add("workflow"); return Task.FromResult(text + " transformed"); },
            expandSnippets: (text, _) => { steps.Add("snippets"); Assert.Contains("transformed", text); return Task.FromResult(text); },
            boostVocabulary: text => { steps.Add("boost"); return text; },
            correctDictionary: text => { steps.Add("correct"); return text; });
        Assert.Equal(new[] { "workflow", "snippets", "boost", "correct" }, steps);
        Assert.Null(result.WorkflowError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrFailedWorkflowRetainsOriginalText(bool fail)
    {
        var result = await DictationTextPipeline.ProcessAsync("original transcript", new(), "en",
            workflow: (_, _) => fail ? Task.FromException<string>(new IOException("private provider details")) : Task.FromResult(" "));
        Assert.Equal("original transcript", result.Text);
        Assert.NotNull(result.WorkflowError);
        Assert.DoesNotContain("private provider details", result.WorkflowError);
    }

    [Fact]
    public async Task LateWorkflowResultIsRejectedAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = DictationTextPipeline.ProcessAsync("source", new(), "en", ct: cancellation.Token,
            workflow: async (_, _) => { entered.SetResult(); return await release.Task; });
        await entered.Task;
        cancellation.Cancel();
        release.SetResult("late result");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
