using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ManualWorkflowTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workflow-templates-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_directory, "workflows.json");
    public static IEnumerable<object[]> Templates => WorkflowTemplateCatalog.All.Select(item => new object[] { item.Template });
    private static Workflow Workflow(WorkflowTemplate template) => new()
    {
        Id = Guid.NewGuid().ToString(), Name = "Template draft", Template = template, Trigger = WorkflowTrigger.Manual(),
        Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", FineTuning = template == WorkflowTemplate.Custom ? "Preserve names." : "" }
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public async Task EveryCatalogTemplatePersistsAndRunsTheExactCorePrompt(WorkflowTemplate template)
    {
        var workflow = Workflow(template);
        var draft = WorkflowDraft.FromStored(workflow);
        var store = new ManualWorkflowStore(StorePath);
        Assert.True(ManualWorkflowStore.IsSupported(workflow));
        store.Save(draft.ToStored());
        var restored = Assert.Single(new ManualWorkflowStore(StorePath).Read());
        Assert.Equal(template, restored.Template);
        Assert.Equal(workflow.Behavior.FineTuning, restored.Behavior.FineTuning);
        var calls = 0;
        var result = await ManualWorkflowRunner.RunAsync(restored, "Exact source", (_, _) => true,
            (provider, prompt, input, model, _) =>
            {
                calls++;
                Assert.Equal("provider", provider);
                Assert.Equal("model", model);
                Assert.Equal(workflow.SystemPrompt(), prompt);
                Assert.Equal("Exact source", input);
                return Task.FromResult("Real adapter test output");
            });
        Assert.Equal(1, calls);
        Assert.Equal("Real adapter test output", result);
    }

    [Theory]
    [InlineData(null, "English")]
    [InlineData("German", "German")]
    [InlineData("日本語", "日本語")]
    public void TranslationTargetRoundTripsAndFeedsCorePrompt(string? target, string expected)
    {
        var draft = WorkflowDraft.FromStored(Workflow(WorkflowTemplate.Translation)) with { TranslationTarget = target, Instruction = "Keep names unchanged." };
        var store = new ManualWorkflowStore(StorePath);
        store.Save(draft.ToStored());
        var restored = WorkflowDraft.FromStored(Assert.Single(store.Read()));
        Assert.Equal(target, restored.TranslationTarget);
        Assert.Contains($"into {expected}", restored.ToStored().SystemPrompt());
        Assert.Contains("Keep names unchanged.", restored.ToStored().SystemPrompt());
        Assert.Contains("Target language: " + expected, restored.InstructionDescription);
    }

    [Fact]
    public void CustomInstructionsRemainRequiredAndFailedValidationDoesNotWrite()
    {
        var store = new ManualWorkflowStore(StorePath);
        var custom = Workflow(WorkflowTemplate.Custom);
        store.Save(custom);
        var bytes = File.ReadAllBytes(StorePath);
        Assert.Throws<ArgumentException>(() => store.Save(custom with { Behavior = custom.Behavior with { FineTuning = "  " } }));
        Assert.Equal(bytes, File.ReadAllBytes(StorePath));
        Assert.Throws<InvalidOperationException>(() => store.Save(custom with { Template = (WorkflowTemplate)999 }));
        Assert.Equal(bytes, File.ReadAllBytes(StorePath));
    }

    [Fact]
    public void TemplateDraftPreservesUneditedCoreMetadataAndDiscardLeavesOriginalUntouched()
    {
        var original = Workflow(WorkflowTemplate.Translation) with
        {
            SortOrder = 17, CreatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Output = new() { AutoEnter = true },
            Behavior = new() { ProviderOverride = "provider", ModelOverride = "model", InputLanguage = "de", InputLanguageHints = ["de", "en"], TranslationTarget = "French" }
        };
        var opened = WorkflowDraft.FromStored(original);
        var edited = opened with { Template = WorkflowTemplate.Summary, Instruction = "Keep all dates." };
        var stored = edited.ToStored();
        Assert.Equal(WorkflowTemplate.Translation, opened.Template);
        Assert.Equal("", opened.Instruction);
        Assert.Equal(WorkflowTemplate.Summary, stored.Template);
        Assert.Equal("French", stored.Behavior.TranslationTarget);
        Assert.Same(original.Trigger, stored.Trigger);
        Assert.Same(original.Output, stored.Output);
        Assert.Same(original.Behavior.Settings, stored.Behavior.Settings);
        Assert.Equal(original.Behavior.InputLanguageHints, stored.Behavior.InputLanguageHints);
        Assert.Equal(original.Behavior.InputLanguage, stored.Behavior.InputLanguage);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
        Assert.Equal(original.SortOrder, stored.SortOrder);
    }

    [Fact]
    public void UnsupportedAutomaticActionAndSettingsWorkflowsRemainUnchanged()
    {
        var supported = Workflow(WorkflowTemplate.Summary);
        var automatic = Workflow(WorkflowTemplate.Translation) with { Trigger = WorkflowTrigger.App("editor") };
        var action = Workflow(WorkflowTemplate.Checklist) with { Output = new() { TargetActionPluginId = "action" } };
        var settings = Workflow(WorkflowTemplate.Json) with { Behavior = new() { Settings = new() { ["schema"] = "keep this" } } };
        Assert.True(new WorkflowService(StorePath).TryReplaceAll([automatic, action, settings]));
        var store = new ManualWorkflowStore(StorePath);
        foreach (var unsupported in new[] { automatic, action, settings })
        {
            Assert.False(ManualWorkflowStore.IsSupported(unsupported));
            Assert.Throws<InvalidOperationException>(() => store.Save(unsupported));
            Assert.Throws<InvalidOperationException>(() => store.Delete(unsupported.Id));
        }
        store.Save(supported);
        var saved = store.Read();
        var savedTrigger = saved.Single(item => item.Id == automatic.Id).Trigger;
        Assert.Equal(automatic.Trigger.Kind, savedTrigger.Kind);
        Assert.Equal(automatic.Trigger.ProcessNames, savedTrigger.ProcessNames);
        Assert.Equal(action.Output, saved.Single(item => item.Id == action.Id).Output);
        Assert.Equal("keep this", saved.Single(item => item.Id == settings.Id).Behavior.Settings["schema"]);
        Assert.Equal(4, saved.Count);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
