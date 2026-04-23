using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public sealed class WorkflowServiceTests : IDisposable
{
    private readonly string _filePath;
    private readonly WorkflowService _sut;

    public WorkflowServiceTests()
    {
        _filePath = Path.GetTempFileName();
        _sut = new WorkflowService(_filePath);
    }

    [Fact]
    public void AddWorkflow_PersistsAndLoads()
    {
        var workflow = NewWorkflow("Mail", WorkflowTrigger.App("OUTLOOK"));

        _sut.AddWorkflow(workflow);

        var freshService = new WorkflowService(_filePath);
        var loaded = Assert.Single(freshService.Workflows);
        Assert.Equal("Mail", loaded.Name);
        Assert.Equal(WorkflowTemplate.CleanedText, loaded.Template);
        Assert.Equal("OUTLOOK", loaded.Trigger.ProcessNames[0]);
    }

    [Fact]
    public void ToggleWorkflow_UpdatesEnabledState()
    {
        var workflow = NewWorkflow("Mail", WorkflowTrigger.App("OUTLOOK"));
        _sut.AddWorkflow(workflow);

        _sut.ToggleWorkflow(workflow.Id);

        Assert.False(_sut.Workflows.Single().IsEnabled);
    }

    [Fact]
    public void Reorder_UpdatesSortOrder()
    {
        var first = NewWorkflow("First", WorkflowTrigger.App("a"), sortOrder: 0);
        var second = NewWorkflow("Second", WorkflowTrigger.App("b"), sortOrder: 1);
        var third = NewWorkflow("Third", WorkflowTrigger.App("c"), sortOrder: 2);
        _sut.AddWorkflow(first);
        _sut.AddWorkflow(second);
        _sut.AddWorkflow(third);

        _sut.Reorder([third.Id, first.Id, second.Id]);

        Assert.Equal(["Third", "First", "Second"], _sut.Workflows.Select(w => w.Name).ToArray());
        Assert.Equal([0, 1, 2], _sut.Workflows.Select(w => w.SortOrder).ToArray());
    }

    [Fact]
    public void MatchWorkflow_WebsiteWinsOverApp()
    {
        _sut.AddWorkflow(NewWorkflow("App", WorkflowTrigger.App("chrome"), sortOrder: 0));
        _sut.AddWorkflow(NewWorkflow("Website", WorkflowTrigger.Website("github.com"), sortOrder: 9));

        var match = _sut.MatchWorkflow("chrome", "https://github.com/TypeWhisper/typewhisper-win");

        Assert.NotNull(match);
        Assert.Equal("Website", match.Workflow.Name);
        Assert.Equal(WorkflowMatchKind.Website, match.Kind);
    }

    [Fact]
    public void MatchWorkflow_UsesLowestSortOrderAmongSameTriggerType()
    {
        _sut.AddWorkflow(NewWorkflow("Later", WorkflowTrigger.Website("github.com"), sortOrder: 5));
        _sut.AddWorkflow(NewWorkflow("Earlier", WorkflowTrigger.Website("github.com"), sortOrder: 1));

        var match = _sut.MatchWorkflow("chrome", "https://www.github.com/path");

        Assert.NotNull(match);
        Assert.Equal("Earlier", match.Workflow.Name);
        Assert.Equal(1, match.CompetingWorkflowCount);
        Assert.True(match.WonBySortOrder);
    }

    [Fact]
    public void MatchWorkflow_IgnoresDisabledWorkflows()
    {
        _sut.AddWorkflow(NewWorkflow("Disabled", WorkflowTrigger.App("notepad"), isEnabled: false));

        Assert.Null(_sut.MatchWorkflow("notepad", null));
    }

    [Fact]
    public void ForceMatch_ReturnsManualOverrideForEnabledWorkflow()
    {
        var workflow = NewWorkflow("Manual", WorkflowTrigger.Hotkey("Ctrl+Alt+1"));
        _sut.AddWorkflow(workflow);

        var match = _sut.ForceMatch(workflow.Id);

        Assert.NotNull(match);
        Assert.Equal(WorkflowMatchKind.ManualOverride, match.Kind);
        Assert.Equal("Manual", match.Workflow.Name);
    }

    [Fact]
    public void SystemPrompt_CustomRequiresInstruction()
    {
        var workflow = NewWorkflow("Custom", WorkflowTrigger.Hotkey("Ctrl+Alt+1")) with
        {
            Template = WorkflowTemplate.Custom
        };

        Assert.Null(workflow.SystemPrompt());
    }

    [Fact]
    public void SystemPrompt_UsesWorkflowBehaviorAndOutput()
    {
        var workflow = NewWorkflow("Translate", WorkflowTrigger.App("slack")) with
        {
            Template = WorkflowTemplate.Translation,
            Behavior = new WorkflowBehavior
            {
                Settings = new Dictionary<string, string> { ["targetLanguage"] = "German" },
                FineTuning = "Keep product names unchanged."
            },
            Output = new WorkflowOutput { Format = "plain text" }
        };

        var prompt = workflow.SystemPrompt(configuredLanguage: "en");

        Assert.NotNull(prompt);
        Assert.Contains("German", prompt);
        Assert.Contains("Keep product names unchanged.", prompt);
        Assert.Contains("plain text", prompt);
    }

    private static Workflow NewWorkflow(
        string name,
        WorkflowTrigger trigger,
        int sortOrder = 0,
        bool isEnabled = true) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            IsEnabled = isEnabled,
            SortOrder = sortOrder,
            Template = WorkflowTemplate.CleanedText,
            Trigger = trigger
        };

    public void Dispose()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
