using TypeWhisper.Core.Models;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class WorkflowLlmDefaultsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workflow-defaults-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "defaults.json");
    private static Workflow Draft(string? provider = WorkflowLlmDefaults.Inherit, string? model = null) => new()
    {
        Id = "test", Name = "Translate", Template = WorkflowTemplate.Translation,
        Trigger = WorkflowTrigger.Hotkey(["CTRL+J"], WorkflowHotkeyBehavior.StartDictation),
        Behavior = new() { ProviderOverride = provider, ModelOverride = model }
    };
    [Fact]
    public void SavedPairSurvivesRestartAndDoesNotRewriteTheWorkflow()
    {
        var store = new WorkflowLlmDefaults(FilePath);
        Assert.Null(store.Read());
        store.Save(new("provider", "model"));
        var original = Draft();
        var resolved = new WorkflowLlmDefaults(FilePath).Resolve(original);
        Assert.Equal("provider", resolved.Behavior.ProviderOverride);
        Assert.Equal("model", resolved.Behavior.ModelOverride);
        Assert.Equal(WorkflowLlmDefaults.Inherit, original.Behavior.ProviderOverride);
    }
    [Fact]
    public void ExplicitSelectionNeverFallsBackEvenWhenDefaultsAreCorrupt()
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(FilePath, "invalid json");
        var explicitWorkflow = Draft("own", "own-model");
        Assert.Same(explicitWorkflow, new WorkflowLlmDefaults(FilePath).Resolve(explicitWorkflow));
        Assert.Throws<System.Text.Json.JsonException>(() => new WorkflowLlmDefaults(FilePath).Resolve(Draft()));
    }
    [Fact]
    public void MissingDefaultStaysUnavailableAndLegacyMissingSelectionDoesNotOptIn()
    {
        var resolved = new WorkflowLlmDefaults(FilePath).Resolve(Draft());
        Assert.NotNull(ManualWorkflowRunner.ConfigurationError(resolved.Behavior.ProviderOverride, resolved.Behavior.ModelOverride, (_, _) => true));
        var legacy = Draft(null);
        Assert.Same(legacy, WorkflowLlmDefaults.Apply(legacy, new("provider", "model")));
    }
    [Fact]
    public async Task RecordingSnapshotKeepsItsModelWhenTheDefaultChanges()
    {
        var store = new WorkflowLlmDefaults(FilePath);
        store.Save(new("first", "model-one"));
        var snapshot = AutomaticWorkflowSnapshot.ForDictationShortcut(store.Resolve(Draft()));
        store.Save(new("second", "model-two"));
        var result = await snapshot.ProcessAsync("text", null, null, (_, _) => true,
            (p, prompt, text, model, ct) => Task.FromResult(p + "/" + model), default);
        Assert.Equal("first/model-one", result);
        Assert.Equal("second", store.Resolve(Draft()).Behavior.ProviderOverride);
    }
    [Fact]
    public void AutomaticMatchingResolvesOnlyTheSelectedWorkflow()
    {
        var own = Draft("own", "model") with { Id = "app", Trigger = WorkflowTrigger.App(["notepad"]) };
        var inherited = Draft() with { Id = "fallback", Trigger = WorkflowTrigger.Global() };
        int calls = 0;
        var snapshot = AutomaticWorkflowSnapshot.Select([inherited, own], "notepad", resolve: workflow =>
        { calls++; Assert.Equal("app", workflow.Id); return workflow; });
        Assert.Equal("app", snapshot?.Id); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task ManualExecutionUsesTheResolvedPair()
    {
        var resolved = WorkflowLlmDefaults.Apply(Draft(), new("default-provider", "default-model"));
        var result = await ManualWorkflowRunner.RunAsync(resolved, "hello", (_, _) => true,
            (p, prompt, text, model, ct) => Task.FromResult(p + "/" + model));
        Assert.Equal("default-provider/default-model", result);
    }
    [Fact]
    public void EnforcedPairReplacesEverySelectionAndBlocksSaving()
    {
        new WorkflowLlmDefaults(FilePath).Save(new("saved", "saved-model"));
        WorkflowLlmSelection? policy = new("required", "required-model");
        var store = new WorkflowLlmDefaults(FilePath, () => policy);

        Assert.Equal(policy, store.Read());
        Assert.Equal(new WorkflowLlmSelection("saved", "saved-model"), store.ReadSaved());
        Assert.Equal(policy, store.Effective("own", "own-model"));
        Assert.Equal(policy, store.Effective(WorkflowLlmDefaults.Inherit, ""));
        var own = store.Resolve(Draft("own", "own-model"));
        Assert.Equal("required", own.Behavior.ProviderOverride);
        Assert.Equal("required-model", own.Behavior.ModelOverride);
        Assert.Equal("required", store.Resolve(Draft()).Behavior.ProviderOverride);
        Assert.Equal("required", store.Resolve(Draft(null)).Behavior.ProviderOverride);
        var dictation = Draft("own", "own-model") with { Template = WorkflowTemplate.Dictation };
        Assert.Same(dictation, store.Resolve(dictation));
        Assert.Throws<InvalidOperationException>(() => store.Save(new("other", "model")));
        Assert.Equal(new WorkflowLlmSelection("saved", "saved-model"), store.ReadSaved());

        // Removing the policy reveals the saved default and the workflow's own selection again.
        policy = null;
        Assert.Equal(new WorkflowLlmSelection("saved", "saved-model"), store.Read());
        Assert.Equal("own", store.Resolve(Draft("own", "own-model")).Behavior.ProviderOverride);
        Assert.Equal("saved", store.Resolve(Draft()).Behavior.ProviderOverride);
    }
    [Fact]
    public void RecommendedPairIsTheDefaultUntilTheUserSavesOne()
    {
        var store = new WorkflowLlmDefaults(FilePath, recommended: () => new("suggested", "suggested-model"));

        Assert.Equal(new WorkflowLlmSelection("suggested", "suggested-model"), store.Read());
        Assert.Null(store.ReadSaved());
        Assert.Equal("suggested", store.Resolve(Draft()).Behavior.ProviderOverride);
        Assert.Equal("own", store.Resolve(Draft("own", "own-model")).Behavior.ProviderOverride);
        Assert.Equal(new WorkflowLlmSelection("own", "own-model"), store.Effective("own", "own-model"));

        store.Save(new("mine", "my-model"));
        Assert.Equal(new WorkflowLlmSelection("mine", "my-model"), store.Read());
        Assert.Equal("mine", store.Resolve(Draft()).Behavior.ProviderOverride);
    }
    [Fact]
    public void InvalidSavePreservesPreviousPair()
    {
        var store = new WorkflowLlmDefaults(FilePath); store.Save(new("provider", "model"));
        Assert.Throws<InvalidDataException>(() => store.Save(new("none", "")));
        Assert.Equal(new WorkflowLlmSelection("provider", "model"), store.Read());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
